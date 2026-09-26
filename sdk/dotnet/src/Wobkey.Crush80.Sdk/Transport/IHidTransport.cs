namespace Wobkey.Crush80.Transport;

/// <summary>Exchanges canonical 32-byte VIA payloads with a single HID device.</summary>
public interface IHidTransport : IAsyncDisposable
{
    /// <summary>Gets the device represented by this transport.</summary>
    Crush80DeviceDescriptor Device { get; }

    /// <summary>Writes exactly 32 payload bytes, without the HID report ID.</summary>
    /// <exception cref="ArgumentException">The payload is not 32 bytes long.</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Reads exactly 32 payload bytes, without the HID report ID.</summary>
    /// <exception cref="ArgumentException">The payload is not 32 bytes long.</exception>
    ValueTask ReadAsync(Memory<byte> payload, TimeSpan timeout, CancellationToken cancellationToken = default);
}
