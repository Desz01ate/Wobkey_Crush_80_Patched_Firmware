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

/// <summary>A restoration step that could not be completed.</summary>
public sealed record StateRestoreFailure(string Field, Exception Error);

/// <summary>The SDK was unable to restore the device's captured lighting state.</summary>
public sealed class StateRestoreException : Crush80SdkException
{
    /// <summary>Initializes an exception with failures in restoration order.</summary>
    public StateRestoreException(
        IReadOnlyList<StateRestoreFailure> failures,
        Exception? originalOperationFailure = null)
        : base("The keyboard state could not be restored completely.",
            operation: "RestoreState", innerException: originalOperationFailure)
    {
        Failures = failures.ToArray();
    }

    /// <summary>Gets the restoration steps that failed or could not safely run.</summary>
    public IReadOnlyList<StateRestoreFailure> Failures { get; }
}
