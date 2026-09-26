namespace Wobkey.Crush80;

/// <summary>No matching wired Crush 80 device was found.</summary>
public sealed class DeviceNotFoundException : Crush80SdkException
{
    /// <summary>Initializes an exception for an unsuccessful device lookup.</summary>
    public DeviceNotFoundException(
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base("No matching Crush 80 device was found.", operation, device, innerException)
    {
    }
}

/// <summary>The device could not be opened because it is already in use.</summary>
public sealed class DeviceBusyException : Crush80SdkException
{
    /// <summary>Initializes an exception for a device that is already in use.</summary>
    public DeviceBusyException(
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base("The Crush 80 device is already in use.", operation, device, innerException)
    {
    }
}

/// <summary>The operating system denied access to the device interface.</summary>
public sealed class DeviceAccessDeniedException : Crush80SdkException
{
    /// <summary>Initializes an exception for a device that cannot be accessed.</summary>
    public DeviceAccessDeniedException(
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base("Access to the Crush 80 device was denied.", operation, device, innerException)
    {
    }
}

/// <summary>The connected firmware does not implement the required PKRG protocol.</summary>
public sealed class IncompatibleFirmwareException : Crush80SdkException
{
    /// <summary>Initializes an exception for firmware that does not satisfy the SDK protocol.</summary>
    public IncompatibleFirmwareException(
        string message = "The device firmware is incompatible with the Crush 80 SDK.",
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base(message, operation, device, innerException)
    {
    }
}

/// <summary>The firmware rejected a structurally valid protocol request.</summary>
public sealed class FirmwareRejectedRequestException : Crush80SdkException
{
    /// <summary>Initializes an exception with the firmware's nonzero status byte.</summary>
    public FirmwareRejectedRequestException(
        byte status,
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base(CreateMessage(status), operation, device, innerException)
    {
        Status = status;
    }

    /// <summary>Gets the firmware status byte.</summary>
    public byte Status { get; }

    private static string CreateMessage(byte status) => status switch
    {
        1 => "The firmware rejected the request because the operation is unsupported (status 1).",
        2 => "The firmware rejected the request because the LED range is invalid (status 2).",
        3 => "The firmware rejected the request because the mode is invalid (status 3).",
        _ => $"The firmware rejected the request with unknown status {status}."
    };
}

/// <summary>A connected device disappeared or became unavailable during an operation.</summary>
public sealed class DeviceDisconnectedException : Crush80SdkException
{
    /// <summary>Initializes an exception for a device that disconnected.</summary>
    public DeviceDisconnectedException(
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base("The Crush 80 device disconnected.", operation, device, innerException)
    {
    }
}

/// <summary>The session cannot continue after a previous operation faulted it.</summary>
public sealed class SessionFaultedException : Crush80SdkException
{
    /// <summary>Initializes an exception for an unusable session.</summary>
    public SessionFaultedException(
        string? operation = null,
        Crush80DeviceDescriptor? device = null,
        Exception? innerException = null)
        : base("The Crush 80 session is faulted and cannot continue.", operation, device, innerException)
    {
    }
}

/// <summary>The SDK was unable to restore the device's captured lighting state.</summary>
public sealed class StateRestoreException : Crush80SdkException
{
    /// <summary>Initializes an exception for an incomplete state restoration.</summary>
    public StateRestoreException(Exception? innerException = null)
        : base("The keyboard state could not be restored completely.", "RestoreState", innerException: innerException)
    {
    }
}
