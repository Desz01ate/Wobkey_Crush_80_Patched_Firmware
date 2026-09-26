using System.Buffers.Binary;
using System.Text;

namespace Wobkey.Crush80.Emulator;

internal static class Crush80IpcProtocol
{
    internal const byte Version = 1;
    internal const byte WriteOperation = 1;
    internal const byte ReadOperation = 2;
    internal const byte CloseOperation = 3;
    internal const int HidPayloadLength = 32;
    internal const int MaximumErrorLength = 4096;
    private static readonly byte[] SuccessResponse = [(byte)Status.Success];

    internal static ReadOnlySpan<byte> Magic => "C80E"u8;

    internal enum Status : byte
    {
        Success = 0,
        Timeout = 1,
        InvalidOperation = 2,
        ServerError = 3,
        Busy = 4,
        IncompatibleVersion = 5
    }

    internal static async ValueTask WriteSuccessAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(SuccessResponse, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask WriteErrorAsync(
        Stream stream,
        Status status,
        string message,
        CancellationToken cancellationToken)
    {
        var encoded = Encoding.UTF8.GetBytes(message);
        if (encoded.Length > MaximumErrorLength)
            encoded = encoded[..MaximumErrorLength];
        var header = new byte[3];
        header[0] = (byte)status;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)encoded.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<string> ReadErrorAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        if (length > MaximumErrorLength)
            throw new IOException("The emulator server returned an oversized error response.");
        if (length == 0)
            return "The emulator server rejected the operation.";
        var encoded = new byte[length];
        await stream.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(encoded);
    }

    internal static Exception CreateRemoteException(Status status, string message) => status switch
    {
        Status.Timeout => new TimeoutException(message),
        Status.InvalidOperation => new InvalidOperationException(message),
        Status.Busy => new IOException(message),
        Status.IncompatibleVersion => new IOException(message),
        _ => new IOException(message)
    };
}
