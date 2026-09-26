namespace Wobkey.Crush80;

/// <summary>A device response violated the expected PKRG/VIA protocol contract.</summary>
public sealed class ProtocolViolationException : Crush80SdkException
{
    /// <summary>Initializes an exception for a malformed or unexpected response.</summary>
    public ProtocolViolationException(
        string message,
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base(message, operation, device, innerException)
    {
    }
}
