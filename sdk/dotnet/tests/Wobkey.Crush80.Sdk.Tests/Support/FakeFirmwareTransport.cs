using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80.Sdk.Tests.Support;

internal sealed class FakeFirmwareTransport : IHidTransport
{
    internal byte ProtocolVersion { get; set; } = 1;
    internal byte LedCount { get; set; } = 92;
    internal byte ChunkLimit { get; set; } = 8;
    private readonly byte[] _pending = new byte[32];
    private readonly TaskCompletionSource<bool> _firstWriteObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hasPending;

    private Rgb24[] _colors = new Rgb24[92];
    internal Rgb24[] Colors { get => _colors; set => _colors = (Rgb24[])value.Clone(); }
    public Crush80DeviceDescriptor Device { get; } = new(
        "fake://crush80-via", 0x320F, 0x5055, 0xFF60, 0x61, null, "Fake Crush 80");
    internal bool Enabled { get; set; }
    internal byte Brightness { get; set; } = 9;
    internal byte Effect { get; set; } = 7;
    internal byte? RejectNextStatus { get; set; }
    internal int? RejectRgbStartOnce { get; set; }
    internal bool? RejectModeValueOnce { get; set; }
    internal string? MalformOperationOnce { get; set; }
    internal bool TimeoutNextRead { get; set; }
    internal bool PauseAfterWrite { get; set; }
    internal bool CancelCallerAfterNextWrite { get; set; }
    internal CancellationTokenSource? CallerCancellation { get; set; }
    internal int PendingReplyCount => _hasPending ? 1 : 0;
    internal bool IsDisposed { get; private set; }
    internal Task FirstWriteObserved => _firstWriteObserved.Task;
    internal List<string> Operations { get; } = [];
    internal List<int> RgbWriteCounts { get; } = [];
    internal List<Rgb24> RgbWriteFirstColors { get; } = [];

    internal void ReleaseWrites() => _releaseWrites.TrySetResult(true);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length != 32)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(payload));
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_hasPending)
            throw new InvalidOperationException("A firmware response is still pending.");

        payload.CopyTo(_pending);
        Operations.Add(DecodeOperation(_pending));
        _hasPending = true;
        if (CancelCallerAfterNextWrite)
        {
            CancelCallerAfterNextWrite = false;
            CallerCancellation?.Cancel();
        }
        if (PauseAfterWrite)
        {
            _firstWriteObserved.TrySetResult(true);
            await _releaseWrites.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask ReadAsync(Memory<byte> payload, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (payload.Length != 32)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(payload));
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_hasPending)
            throw new InvalidOperationException("There is no pending firmware request.");
        if (TimeoutNextRead)
        {
            TimeoutNextRead = false;
            _hasPending = false;
            throw new TimeoutException("The simulated firmware did not respond in time.");
        }

        var reply = payload.Span;
        _pending.CopyTo(reply);
        var operation = DecodeOperation(_pending);
        var command = _pending[0];
        var channel = _pending[1];
        var id = _pending[2];
        if (channel == 0x7F)
        {
            var status = GetStatus(command, id);
            if (status == 0 && RejectNextStatus is { } overrideStatus)
            {
                status = overrideStatus;
                RejectNextStatus = null;
            }
            reply[3] = status;
            if (status == 0)
                ApplyPkrg(command, id, reply);
        }
        else if (channel == 3)
        {
            if (id == 1)
            {
                if (command == 7 && _pending[3] <= 9)
                    Brightness = _pending[3];
                reply[3] = Brightness;
            }
            else if (id == 2)
            {
                if (command == 7 && _pending[3] <= 18)
                    Effect = _pending[3];
                reply[3] = Effect;
            }
        }

        if (MalformOperationOnce == operation)
        {
            reply[2] ^= 0x40;
            MalformOperationOnce = null;
        }
        _hasPending = false;
        return ValueTask.CompletedTask;
    }

    private byte GetStatus(byte command, byte id)
    {
        if ((command != 7 && command != 8) || (id != 0 && id != 1 && id != 2) ||
            (id == 0 && command != 8))
            return 1;
        if (command == 7 && id == 1 && _pending[4] > 1)
            return 3;
        if (command == 7 && id == 1 && RejectModeValueOnce == (_pending[4] != 0))
        {
            RejectModeValueOnce = null;
            return 3;
        }
        if (id == 2)
        {
            var start = _pending[4];
            var count = _pending[5];
            if (RejectRgbStartOnce == start)
            {
                RejectRgbStartOnce = null;
                return 2;
            }
            if (count == 0 || count > ChunkLimit || start + count > LedCount || start + count > _colors.Length)
                return 2;
        }
        return 0;
    }

    private void ApplyPkrg(byte command, byte id, Span<byte> reply)
    {
        if (id == 0 && command == 8)
        {
            reply[4] = (byte)'P';
            reply[5] = (byte)'K';
            reply[6] = (byte)'R';
            reply[7] = (byte)'G';
            reply[8] = ProtocolVersion;
            reply[9] = LedCount;
            reply[10] = ChunkLimit;
            reply[11] = Enabled ? (byte)1 : (byte)0;
        }
        else if (id == 1)
        {
            if (command == 7)
                Enabled = _pending[4] != 0;
            reply[4] = Enabled ? (byte)1 : (byte)0;
        }
        else if (id == 2)
        {
            var start = _pending[4];
            var count = _pending[5];
            if (command == 7)
            {
                RgbWriteCounts.Add(count);
                RgbWriteFirstColors.Add(new Rgb24(_pending[6], _pending[7], _pending[8]));
            }
            for (var index = 0; index < count; index++)
            {
                var offset = 6 + index * 3;
                if (command == 7)
                    _colors[start + index] = new Rgb24(_pending[offset], _pending[offset + 1], _pending[offset + 2]);
                else
                {
                    var color = _colors[start + index];
                    reply[offset] = color.Red;
                    reply[offset + 1] = color.Green;
                    reply[offset + 2] = color.Blue;
                }
            }
        }
    }

    private static string DecodeOperation(ReadOnlySpan<byte> request) => (request[0], request[1], request[2]) switch
    {
        (8, 0x7F, 0) => "GetCapabilities",
        (8, 0x7F, 1) => "GetEnabled",
        (7, 0x7F, 1) => "SetEnabled",
        (8, 0x7F, 2) => "ReadRgb",
        (7, 0x7F, 2) => "WriteRgb",
        (8, 3, 1) => "GetBrightness",
        (7, 3, 1) => "SetBrightness",
        (8, 3, 2) => "GetEffect",
        (7, 3, 2) => "SetEffect",
        _ => "Unknown"
    };

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
