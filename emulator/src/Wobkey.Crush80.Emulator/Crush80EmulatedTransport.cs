using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Transport;

namespace Wobkey.Crush80.Emulator;

/// <summary>
/// Stateful in-process implementation of the wired Crush 80 VIA and PKRG v2 protocol.
/// It performs no HID discovery or hardware access.
/// </summary>
public sealed class Crush80EmulatedTransport : IHidTransport
{
    private const int PayloadLength = 32;
    private const int LedCount = 92;
    private const int ChunkLimit = 8;
    private const int StreamLedCount = 9;
    private const int StreamChunkCount = 11;
    private const byte PkrgChannel = 0x7F;
    private const byte OemChannel = 3;

    private readonly object _sync = new();
    private readonly Rgb24[] _colors = new Rgb24[LedCount];
    private readonly byte[] _pendingReply = new byte[PayloadLength];
    private readonly byte[] _stagedFrame = new byte[LedCount * 3];
    private bool _hasPendingReply;
    private bool _disposed;
    private bool _enabled;
    private byte _brightness = 9;
    private byte _effect = 7;
    private byte _streamFrameId;
    private int _nextStreamFragment = -1;
    private long _version;

    /// <inheritdoc />
    public Crush80DeviceDescriptor Device { get; } = new(
        "emulator://crush80-via",
        0x320F,
        0x5055,
        0xFF60,
        0x61,
        null,
        "Emulated Crush 80");

    /// <summary>Gets the latest state version without copying the RGB frame.</summary>
    public long Version
    {
        get
        {
            lock (_sync)
                return _version;
        }
    }

    /// <summary>Captures a stable copy of all observable emulated keyboard state.</summary>
    public Crush80EmulatorState CaptureState()
    {
        lock (_sync)
        {
            return new Crush80EmulatorState(
                _version,
                !_disposed,
                _enabled,
                _brightness,
                _effect,
                (Rgb24[])_colors.Clone());
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length != PayloadLength)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(payload));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hasPendingReply)
                throw new InvalidOperationException("The preceding firmware response has not been read.");

            var request = payload.Span;
            request.CopyTo(_pendingReply);
            var reply = _pendingReply.AsSpan();
            var hasReply = true;
            var changed = request[1] switch
            {
                PkrgChannel => ProcessPkrg(request, reply, out hasReply),
                OemChannel => ProcessOem(request, reply),
                _ => false
            };

            _hasPendingReply = hasReply;
            if (changed)
                _version++;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReadAsync(
        Memory<byte> payload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length != PayloadLength)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(payload));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hasPendingReply)
                throw new TimeoutException("The emulated firmware has no response pending.");

            _pendingReply.CopyTo(payload.Span);
            _hasPendingReply = false;
        }

        return ValueTask.CompletedTask;
    }

    private bool ProcessPkrg(ReadOnlySpan<byte> request, Span<byte> reply, out bool hasReply)
    {
        hasReply = true;
        var command = request[0];
        var operation = request[2];
        switch (operation)
        {
            case 0 when command == 8:
                reply[3..].Clear();
                "PKRG"u8.CopyTo(reply[4..]);
                reply[8] = 2;
                reply[9] = LedCount;
                reply[10] = ChunkLimit;
                reply[11] = _enabled ? (byte)1 : (byte)0;
                reply[12] = StreamLedCount;
                reply[13] = StreamChunkCount;
                return false;

            case 1:
                reply[3] = 0;
                if (command == 8)
                {
                    reply[4] = _enabled ? (byte)1 : (byte)0;
                    return false;
                }
                if (command != 7)
                {
                    reply[3] = 1;
                    return false;
                }
                if (request[4] > 1)
                {
                    reply[3] = 3;
                    return false;
                }

                var enabled = request[4] == 1;
                var modeChanged = enabled != _enabled;
                _enabled = enabled;
                reply[4] = request[4];
                return modeChanged;

            case 2:
                return ProcessRgbChunk(request, reply);

            case 3:
                hasReply = false;
                ProcessStreamFragment(request);
                return false;

            case 4:
                return ProcessStreamCommit(request, reply);

            default:
                reply[3] = 1;
                return false;
        }
    }

    private bool ProcessRgbChunk(ReadOnlySpan<byte> request, Span<byte> reply)
    {
        var command = request[0];
        var start = request[4];
        var count = request[5];
        reply[3] = 0;
        if ((command != 7 && command != 8) || count == 0 || count > ChunkLimit || start + count > LedCount)
        {
            reply[3] = command is 7 or 8 ? (byte)2 : (byte)1;
            return false;
        }

        var changed = false;
        for (var index = 0; index < count; index++)
        {
            var payloadOffset = 6 + index * 3;
            var colorIndex = start + index;
            if (command == 7)
            {
                var color = new Rgb24(request[payloadOffset], request[payloadOffset + 1], request[payloadOffset + 2]);
                changed |= color != _colors[colorIndex];
                _colors[colorIndex] = color;
            }
            else
            {
                var color = _colors[colorIndex];
                reply[payloadOffset] = color.Red;
                reply[payloadOffset + 1] = color.Green;
                reply[payloadOffset + 2] = color.Blue;
            }
        }

        return changed;
    }

    private void ProcessStreamFragment(ReadOnlySpan<byte> request)
    {
        if (request[0] != 7)
            return;

        var frameId = request[3];
        var fragment = request[4];
        if (fragment == 0)
        {
            _streamFrameId = frameId;
            _nextStreamFragment = 0;
        }

        if (frameId != _streamFrameId || fragment != _nextStreamFragment || fragment >= StreamChunkCount)
        {
            _nextStreamFragment = -1;
            return;
        }

        var byteCount = fragment == StreamChunkCount - 1 ? 6 : StreamLedCount * 3;
        request.Slice(5, byteCount).CopyTo(_stagedFrame.AsSpan(fragment * StreamLedCount * 3));
        _nextStreamFragment++;
    }

    private bool ProcessStreamCommit(ReadOnlySpan<byte> request, Span<byte> reply)
    {
        reply[3] = 0;
        if (request[0] != 7)
        {
            reply[3] = 1;
            return false;
        }
        if (_nextStreamFragment != StreamChunkCount || request[4] != _streamFrameId)
        {
            reply[3] = 4;
            return false;
        }

        var changed = false;
        for (var index = 0; index < LedCount; index++)
        {
            var offset = index * 3;
            var color = new Rgb24(_stagedFrame[offset], _stagedFrame[offset + 1], _stagedFrame[offset + 2]);
            changed |= color != _colors[index];
            _colors[index] = color;
        }

        _nextStreamFragment = -1;
        return changed;
    }

    private bool ProcessOem(ReadOnlySpan<byte> request, Span<byte> reply)
    {
        var command = request[0];
        var id = request[2];
        if (command == 8)
        {
            reply[3] = id switch
            {
                1 => _brightness,
                2 => _effect,
                _ => reply[3]
            };
            return false;
        }
        if (command != 7)
            return false;

        if (id == 1 && request[3] <= 9)
        {
            var changed = _brightness != request[3];
            _brightness = request[3];
            return changed;
        }
        if (id == 2 && request[3] <= 18)
        {
            var changed = _effect != request[3];
            _effect = request[3];
            return changed;
        }

        return false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _hasPendingReply = false;
                _version++;
            }
        }

        return ValueTask.CompletedTask;
    }
}
