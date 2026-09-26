using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80.Sdk.Tests.Transport;

public sealed class HidSharpTransportTests
{
    private static readonly Crush80DeviceDescriptor Device = new(
        "exact-path", 0x320F, 0x5055, 0xFF60, 0x61, null, null);

    [Fact]
    public void OpeningPermissionFailureIncludesDescriptor()
    {
        var original = new UnauthorizedAccessException("denied");
        var error = HidSharpTransport.MapOpenFailure(Device, original);
        Assert.IsType<DeviceAccessDeniedException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void TransferPermissionFailureIncludesDescriptor()
    {
        var original = new UnauthorizedAccessException("denied");
        var error = HidSharpTransport.MapTransferFailure(Device, "Read", original);
        Assert.IsType<DeviceAccessDeniedException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void OpeningBusyFailureIncludesDescriptor()
    {
        var original = new IOException("exclusive open failed");
        var error = HidSharpTransport.MapOpenFailure(Device, original);
        Assert.IsType<DeviceBusyException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void TransferFailureIncludesDescriptor()
    {
        var original = new IOException("device removed");
        var error = HidSharpTransport.MapTransferFailure(Device, "Read", original);
        Assert.IsType<DeviceDisconnectedException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void RemovedDeviceFailureIncludesDescriptor()
    {
        var original = new ObjectDisposedException("hid stream");
        var error = HidSharpTransport.MapTransferFailure(Device, "Read", original);
        Assert.IsType<DeviceDisconnectedException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void TransferTimeoutIncludesDescriptor()
    {
        var original = new TimeoutException("timed out");
        var error = HidSharpTransport.MapTransferFailure(Device, "Read", original);
        Assert.IsType<ProtocolViolationException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }
}
