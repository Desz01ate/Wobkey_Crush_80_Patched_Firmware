using HidSharp;

namespace Wobkey.Crush80.Transport;

/// <summary>Adapts HidSharp reports to canonical 32-byte VIA payloads.</summary>
internal sealed class HidSharpTransport : IHidTransport
{
    private readonly HidStream _stream;
    private readonly byte[] _input = new byte[HidReportFraming.ReportLength];
    private readonly byte[] _output = new byte[HidReportFraming.ReportLength];
    private readonly int _timeoutMilliseconds;
    private int _disposed;

    private HidSharpTransport(Crush80DeviceDescriptor device, HidStream stream, TimeSpan timeout)
    {
        Device = device;
        _stream = stream;
        _timeoutMilliseconds = (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
    }

    public Crush80DeviceDescriptor Device { get; }

    internal static HidSharpTransport Open(Crush80DeviceDescriptor descriptor, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        HidDevice device;
        try
        {
            device = Crush80DeviceLocator.FindByPath(descriptor.Path)
                ?? throw new DeviceNotFoundException("Open", descriptor);
            if (device.GetMaxInputReportLength() != HidReportFraming.ReportLength ||
                device.GetMaxOutputReportLength() != HidReportFraming.ReportLength)
                throw new IncompatibleFirmwareException(operation: "Open", device: descriptor);

            var configuration = new OpenConfiguration();
            configuration.SetOption(OpenOption.Exclusive, true);
            return new HidSharpTransport(descriptor, device.Open(configuration),
                timeout ?? new Crush80SessionOptions().ResponseTimeout);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or TimeoutException or ObjectDisposedException)
        {
            throw MapOpenFailure(descriptor, ex);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        HidReportFraming.Encode(payload.Span, _output);
        try
        {
            _stream.WriteTimeout = _timeoutMilliseconds;
            await _stream.WriteAsync(_output, 0, _output.Length, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or TimeoutException or ObjectDisposedException)
        {
            throw MapTransferFailure(Device, "Write", ex);
        }
    }

    public async ValueTask ReadAsync(Memory<byte> payload, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (payload.Length != HidReportFraming.PayloadLength)
            throw new ArgumentException("A VIA payload must be exactly 32 bytes.", nameof(payload));

        try
        {
            _stream.ReadTimeout = (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
            var count = await _stream.ReadAsync(_input, 0, _input.Length, cancellationToken).ConfigureAwait(false);
            if (count != _input.Length)
                throw new ProtocolViolationException("The HID report was truncated.", "Read", Device);
            if (_input[0] != 0)
                throw new ProtocolViolationException("The HID report ID was not zero.", "Read", Device);
            HidReportFraming.Decode(_input, payload.Span);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or TimeoutException or ObjectDisposedException)
        {
            throw MapTransferFailure(Device, "Read", ex);
        }
    }

    internal static Crush80SdkException MapOpenFailure(Crush80DeviceDescriptor descriptor, Exception error) => error switch
    {
        UnauthorizedAccessException => new DeviceAccessDeniedException("Open", descriptor, error),
        FileNotFoundException or DirectoryNotFoundException => new DeviceNotFoundException("Open", descriptor, error),
        TimeoutException => new DeviceBusyException("Open", descriptor, error),
        IOException => new DeviceBusyException("Open", descriptor, error),
        ObjectDisposedException => new DeviceDisconnectedException("Open", descriptor, error),
        _ => throw new ArgumentException("Not an HID open failure.", nameof(error))
    };

    internal static Crush80SdkException MapTransferFailure(Crush80DeviceDescriptor descriptor, string operation, Exception error) => error switch
    {
        UnauthorizedAccessException => new DeviceAccessDeniedException(operation, descriptor, error),
        TimeoutException => new ProtocolViolationException("The HID report transfer timed out.", operation, descriptor, error),
        IOException => new DeviceDisconnectedException(operation, descriptor, error),
        ObjectDisposedException => new DeviceDisconnectedException(operation, descriptor, error),
        _ => throw new ArgumentException("Not an HID transfer failure.", nameof(error))
    };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
