using System.Runtime.ExceptionServices;
using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;
using Wobkey.Crush80.Sdk.Transport;

namespace Wobkey.Crush80.Adapter;

/// <summary>
/// Owns the SDK session and RGB control lease for one wired Crush 80. Opening displays black;
/// changes to <see cref="Grid"/> remain in memory until <see cref="ApplyAsync"/> is called.
/// Dispose the keyboard to restore the device state captured on opening.
/// </summary>
public sealed class Crush80Keyboard : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Crush80RgbSession _session;
    private readonly RgbControlLease _lease;
    private readonly Crush80Grid _grid;
    private Task? _activeApply;
    private Task? _disposeTask;
    private bool _applying;
    private bool _disposed;

    private Crush80Keyboard(Crush80RgbSession session, RgbControlLease lease)
    {
        _session = session;
        _lease = lease;
        _grid = new Crush80Grid(_sync, EnsureMutable);
    }

    /// <summary>Enumerates compatible wired Crush 80 devices in SDK discovery order without opening them.</summary>
    /// <returns>The discovered device descriptors, for selecting an exact interface to open.</returns>
    public static IReadOnlyList<Crush80DeviceDescriptor> EnumerateDevices() => Crush80DeviceLocator.Enumerate();

    /// <summary>Opens the first compatible wired device, captures its state, and displays a black frame.</summary>
    /// <param name="cancellationToken">Cancels session opening or control acquisition.</param>
    /// <returns>A keyboard that owns the session and restores its captured state on disposal.</returns>
    /// <exception cref="DeviceNotFoundException">No compatible wired device was found.</exception>
    public static ValueTask<Crush80Keyboard> OpenAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(Crush80RgbSession.OpenFirstAsync(cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Opens precisely the specified wired device, captures its state, and displays black.</summary>
    /// <param name="descriptor">The descriptor of the interface to open.</param>
    /// <param name="cancellationToken">Cancels session opening or control acquisition.</param>
    /// <returns>A keyboard that owns the session and restores its captured state on disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
    public static ValueTask<Crush80Keyboard> OpenAsync(
        Crush80DeviceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return AcquireAsync(Crush80RgbSession.OpenAsync(descriptor, cancellationToken: cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Opens an injected SDK transport, captures its state, and displays black. This overload is
    /// intended for emulators and controlled integration tests; ownership of the transport transfers
    /// to the returned keyboard.
    /// </summary>
    /// <param name="transport">Transport representing one compatible Crush 80 VIA interface.</param>
    /// <param name="cancellationToken">Cancels session opening or control acquisition.</param>
    public static ValueTask<Crush80Keyboard> OpenAsync(
        IHidTransport transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return AcquireAsync(AdapterSessionFactory.OpenAsync(transport, cancellationToken), cancellationToken);
    }

    private static async ValueTask<Crush80Keyboard> AcquireAsync(
        ValueTask<Crush80RgbSession> sessionOpening, CancellationToken cancellationToken)
    {
        // The SDK owns transport cleanup if opening the session itself fails.
        var session = await sessionOpening.ConfigureAwait(false);
        try
        {
            var black = new Rgb24[Crush80Layout.SlotCount];
            var lease = await session.AcquireControlAsync(black, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new Crush80Keyboard(session, lease);
        }
        catch (Exception acquisitionError)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(acquisitionError, cleanupError);
            }

            throw;
        }
    }

    /// <summary>Gets the physically verified in-memory grid. Edits do not write to the device.</summary>
    public Crush80Grid Grid => _grid;

    /// <summary>
    /// Applies the current grid through the SDK lease. Mutations and additional applies are rejected
    /// until this submission completes; a failed or canceled submission leaves the desired grid intact.
    /// </summary>
    /// <param name="cancellationToken">Cancels the SDK frame submission.</param>
    /// <exception cref="ObjectDisposedException">Disposal has begun.</exception>
    /// <exception cref="InvalidOperationException">Another apply is in flight.</exception>
    public ValueTask ApplyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_applying)
                throw new InvalidOperationException("A grid frame is already being applied.");

            _applying = true;
            ValueTask write;
            try
            {
                write = _lease.WriteFrameAsync(_grid.Frame, cancellationToken);
            }
            catch
            {
                _applying = false;
                throw;
            }

            if (write.IsCompletedSuccessfully)
            {
                _applying = false;
                return write;
            }

            // The continuation holds the frame stable until the SDK has stopped reading it.
            var task = CompleteApplyAsync(write);
            _activeApply = task;
            return new ValueTask(task);
        }
    }

    private async Task CompleteApplyAsync(ValueTask write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
                _applying = false;
        }
    }

    private void EnsureMutable()
    {
        ThrowIfDisposed();
        if (_applying)
            throw new InvalidOperationException("The grid cannot be edited while a frame is being applied.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Stops new edits and applies immediately, waits for any active submission, restores the
    /// captured state without cancellation, and closes the SDK session. Both cleanup failures
    /// are reported if restoration and session closure fail.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync(_applying ? _activeApply : null);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? activeApply)
    {
        if (activeApply is not null)
        {
            try
            {
                await activeApply.ConfigureAwait(false);
            }
            catch
            {
                // The caller of ApplyAsync observes its own error; cleanup must still proceed.
            }
        }

        Exception? leaseError = null;
        Exception? sessionError = null;
        try
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            leaseError = error;
        }

        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            sessionError = error;
        }

        if (leaseError is not null && sessionError is not null)
            throw new AggregateException(leaseError, sessionError);
        if (leaseError is not null)
            ExceptionDispatchInfo.Capture(leaseError).Throw();
        if (sessionError is not null)
            ExceptionDispatchInfo.Capture(sessionError).Throw();
    }
}
