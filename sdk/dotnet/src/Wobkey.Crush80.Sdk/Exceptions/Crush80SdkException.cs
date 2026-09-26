using Wobkey.Crush80.Sdk.Models;

namespace Wobkey.Crush80.Sdk.Exceptions;

/// <summary>Base type for errors reported by the Crush 80 SDK.</summary>
public abstract class Crush80SdkException : Exception
{
    /// <summary>Initializes an SDK exception with optional operation and device context.</summary>
    protected Crush80SdkException(
        string message,
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        Device = device;
    }

    /// <summary>Gets the SDK operation associated with the failure, when available.</summary>
    public string? Operation { get; }

    /// <summary>Gets the device associated with the failure, when available.</summary>
    public Crush80DeviceDescriptor? Device { get; }
}
