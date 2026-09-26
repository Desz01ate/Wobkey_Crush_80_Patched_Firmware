using HidSharp.Reports;
using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Transport;

namespace Wobkey.Crush80.Sdk.Tests.Transport;

public sealed class HidSharpTransportTests
{
    private static readonly Crush80DeviceDescriptor Device = new(
        "exact-path", 0x320F, 0x5055, 0xFF60, 0x61, null, null);

    [Theory]
    [InlineData(false, 32, 32, true)]
    [InlineData(true, 32, 32, false)]
    [InlineData(false, 31, 32, false)]
    [InlineData(false, 32, 31, false)]
    public void OnlyMatchingZeroIdReportsWithThirtyTwoPayloadBytesAreSupported(
        bool numberedReports, byte inputCount, byte outputCount, bool expected)
    {
        var prefix = new byte[] {
            0x06, 0x60, 0xFF, 0x09, 0x61, 0xA1, 0x01,
            0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08 };
        var bytes = prefix.Concat(numberedReports ? new byte[] { 0x85, 0x01 } : []).Concat(
            new byte[] { 0x95, inputCount, 0x81, 0x02, 0x95, outputCount, 0x91, 0x02, 0xC0 }).ToArray();

        Assert.Equal(expected, HidSharpTransport.SupportsZeroIdReports(new ReportDescriptor(bytes)));
    }

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
    public void TransferAccessDeniedHResultIncludesDescriptor()
    {
        var original = new IOException("denied", unchecked((int)0x80070005));
        var error = HidSharpTransport.MapTransferFailure(Device, "Read", original);
        Assert.IsType<DeviceAccessDeniedException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void OpeningSharingViolationIncludesDescriptor(int hresult)
    {
        var original = new IOException("sharing conflict", hresult);
        var error = HidSharpTransport.MapOpenFailure(Device, original);
        Assert.IsType<DeviceBusyException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void OpeningAccessDeniedHResultIncludesDescriptor()
    {
        var original = new IOException("access denied", unchecked((int)0x80070005));
        var error = HidSharpTransport.MapOpenFailure(Device, original);
        Assert.IsType<DeviceAccessDeniedException>(error);
        Assert.Same(Device, error.Device);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void UnclassifiedOpeningIOExceptionPreservesItsCause()
    {
        var original = new IOException("device unplugged");
        var error = HidSharpTransport.MapOpenFailure(Device, original);
        Assert.IsType<DeviceOpenException>(error);
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
