using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Protocol;
using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Protocol;

public sealed class PkrgV1CodecTests
{
    [Fact]
    public void EncodesFinalFourLedChunkAndClearsPadding()
    {
        Span<byte> request = stackalloc byte[PkrgV1Codec.PayloadLength];
        request.Fill(0xA5);
        var colors = new[]
        {
            new Rgb24(1, 2, 3), new Rgb24(4, 5, 6),
            new Rgb24(7, 8, 9), new Rgb24(10, 11, 12)
        };

        PkrgV1Codec.WriteRgbSetRequest(request, 88, colors);

        Assert.Equal(new byte[] { 7, 127, 2, 0, 88, 4 }, request[..6].ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
                     request[6..18].ToArray());
        Assert.All(request[18..].ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void RejectsMismatchedModeAcknowledgement()
    {
        var request = new byte[PkrgV1Codec.PayloadLength];
        PkrgV1Codec.WriteModeSetRequest(request, true);
        request[4] = 0;

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseModeSetResponse(request, true));
    }

    [Fact]
    public void ParsesValidModeReadAndSetResponses()
    {
        var response = new byte[PkrgV1Codec.PayloadLength];
        response[0] = 8;
        response[1] = 127;
        response[2] = 1;
        response[4] = 1;
        Assert.True(PkrgV1Codec.ParseModeGetResponse(response));

        response[4] = 0;
        Assert.False(PkrgV1Codec.ParseModeGetResponse(response));

        response[0] = 7;
        response[1] = 127;
        response[2] = 1;
        response[4] = 1;
        PkrgV1Codec.ParseModeSetResponse(response, true);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void RejectsResponsesWithNoncanonicalLength(int length)
    {
        var response = new byte[length];

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseModeGetResponse(response));
    }

    [Fact]
    public void EveryResponseParserRequiresExactly32Bytes()
    {
        var colors = new[] { new Rgb24(1, 2, 3) };
        Action<byte[]>[] parsers =
        [
            buffer => PkrgV1Codec.ParseCapabilitiesResponse(buffer),
            buffer => PkrgV1Codec.ParseModeGetResponse(buffer),
            buffer => PkrgV1Codec.ParseModeSetResponse(buffer, true),
            buffer => PkrgV1Codec.ParseRgbGetResponse(buffer, 0, colors),
            buffer => PkrgV1Codec.ParseRgbSetResponse(buffer, 0, colors),
            buffer => PkrgV1Codec.ParseBrightnessGetResponse(buffer),
            buffer => PkrgV1Codec.ParseBrightnessSetResponse(buffer, 9),
            buffer => PkrgV1Codec.ParseEffectGetResponse(buffer),
            buffer => PkrgV1Codec.ParseEffectSetResponse(buffer, 6)
        ];

        foreach (var length in new[] { 31, 33 })
        foreach (var parser in parsers)
            Assert.Throws<ProtocolViolationException>(() => parser(new byte[length]));
    }

    [Fact]
    public void EveryRequestWriterRequiresExactly32Bytes()
    {
        var colors = new[] { new Rgb24(1, 2, 3) };
        Action<byte[]>[] writers =
        [
            buffer => PkrgV1Codec.WriteCapabilitiesRequest(buffer),
            buffer => PkrgV1Codec.WriteModeGetRequest(buffer),
            buffer => PkrgV1Codec.WriteModeSetRequest(buffer, true),
            buffer => PkrgV1Codec.WriteRgbGetRequest(buffer, 0, 1),
            buffer => PkrgV1Codec.WriteRgbSetRequest(buffer, 0, colors),
            buffer => PkrgV1Codec.WriteBrightnessGetRequest(buffer),
            buffer => PkrgV1Codec.WriteBrightnessSetRequest(buffer, 9),
            buffer => PkrgV1Codec.WriteEffectGetRequest(buffer),
            buffer => PkrgV1Codec.WriteEffectSetRequest(buffer, 6)
        ];

        foreach (var length in new[] { 31, 33 })
        foreach (var writer in writers)
            Assert.Throws<ArgumentException>(() => writer(new byte[length]));
    }

    [Theory]
    [InlineData(0, 9)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public void RejectsMismatchedResponseCorrelation(int index, byte value)
    {
        var response = new byte[PkrgV1Codec.PayloadLength];
        response[0] = 8;
        response[1] = 127;
        response[2] = 1;
        response[index] = value;

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseModeGetResponse(response));
    }

    [Fact]
    public void RejectsCorrelationMismatchBeforeFirmwareStatus()
    {
        var response = new byte[PkrgV1Codec.PayloadLength];
        response[0] = 8;
        response[1] = 127;
        response[2] = 2;
        response[3] = 1;

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseModeGetResponse(response));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MapsFirmwareStatusToRejectedRequestBeforeReadingModePayload(byte status)
    {
        var response = new byte[PkrgV1Codec.PayloadLength];
        response[0] = 8;
        response[1] = 127;
        response[2] = 1;
        response[3] = status;
        response[4] = 2;

        var exception = Assert.Throws<FirmwareRejectedRequestException>(
            () => PkrgV1Codec.ParseModeGetResponse(response));

        Assert.Equal(status, exception.Status);
        Assert.Equal("GetEnabled", exception.Operation);
        if (status == 4)
            Assert.Contains("incomplete or invalid frame", exception.Message);
        if (status == 5)
            Assert.Contains("commit/acknowledgement is pending", exception.Message);
    }

    [Fact]
    public void RejectsInvalidModeValueAfterValidResponseEnvelope()
    {
        var response = new byte[PkrgV1Codec.PayloadLength];
        response[0] = 8;
        response[1] = 127;
        response[2] = 1;
        response[4] = 2;

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseModeGetResponse(response));
    }

    [Fact]
    public void ParsesVersionOneWithoutStreamingMetadata()
    {
        var response = Convert.FromBase64String("CH8AAFBLUkcCXAgACQsAAAAAAAAAAAAAAAAAAAAAAAA=");
        response[8] = 1;
        response[12] = 0;
        response[13] = 0;

        var capabilities = PkrgV1Codec.ParseCapabilitiesResponse(response);

        Assert.Equal(new PerKeyRgbCapabilities(1, 92, 8, false), capabilities);
        Assert.Null(capabilities.StreamLedCount);
        Assert.Null(capabilities.StreamChunkCount);
        Assert.False(capabilities.SupportsFrameStreaming);
    }

    [Fact]
    public void ParsesExactWindowsOptimizedFirmwareCapabilityResponse()
    {
        var response = Convert.FromBase64String("CH8AAFBLUkcCXAgACQsAAAAAAAAAAAAAAAAAAAAAAAA=");

        var capabilities = PkrgV1Codec.ParseCapabilitiesResponse(response);

        Assert.Equal((byte)2, capabilities.ProtocolVersion);
        Assert.Equal(92, capabilities.LedCount);
        Assert.Equal(8, capabilities.ChunkLimit);
        Assert.False(capabilities.Enabled);
        Assert.Equal(9, capabilities.StreamLedCount);
        Assert.Equal(11, capabilities.StreamChunkCount);
        Assert.True(capabilities.SupportsFrameStreaming);
    }

    [Theory]
    [InlineData(2, 8, 11)]
    [InlineData(2, 9, 10)]
    [InlineData(3, 9, 11)]
    public void RejectsUnsupportedCapabilityFormats(byte version, byte streamLeds, byte streamChunks)
    {
        var response = Convert.FromBase64String("CH8AAFBLUkcCXAgACQsAAAAAAAAAAAAAAAAAAAAAAAA=");
        response[8] = version;
        response[12] = streamLeds;
        response[13] = streamChunks;

        var error = Assert.Throws<IncompatibleFirmwareException>(
            () => PkrgV1Codec.ParseCapabilitiesResponse(response));
        Assert.Contains("version 1 or version 2", error.Message);
        Assert.Contains($"version={version}", error.Message);
        Assert.Contains($"stream LEDs={streamLeds}", error.Message);
        Assert.Contains($"stream fragments={streamChunks}", error.Message);
        Assert.Contains("LEDs=92", error.Message);
        Assert.Contains("chunk limit=8", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CapabilityStatusRejectsAsIncompatibleAfterHeaderValidation(byte status)
    {
        var response = Convert.FromBase64String("CH8AAFBLUkcCXAgACQsAAAAAAAAAAAAAAAAAAAAAAAA=");
        response[3] = status;
        var incompatible = Assert.Throws<IncompatibleFirmwareException>(
            () => PkrgV1Codec.ParseCapabilitiesResponse(response));
        Assert.Equal("GetCapabilities", incompatible.Operation);
        Assert.Contains($"status {status}", incompatible.Message);

        response[2] = 2;
        Assert.Throws<ProtocolViolationException>(() => PkrgV1Codec.ParseCapabilitiesResponse(response));
        Assert.Throws<ProtocolViolationException>(() => PkrgV1Codec.ParseCapabilitiesResponse(response[..31]));
    }

    [Fact]
    public async Task FakeRejectsOversizedRawRgbChunkEvenWhenAdvertisedLimitIsLarger()
    {
        await using var transport = new FakeFirmwareTransport { ChunkLimit = 12 };
        var request = new byte[32];
        request[0] = 7;
        request[1] = 0x7F;
        request[2] = 2;
        request[5] = 9;
        var response = new byte[32];

        await transport.WriteAsync(request);
        await transport.ReadAsync(response, TimeSpan.FromSeconds(1));

        Assert.Equal((byte)2, response[3]);
        Assert.Equal(new Rgb24[92], transport.Colors);
    }

    [Fact]
    public void ParsesRgbReadOnlyAfterMatchingRangeEcho()
    {
        var response = new byte[PkrgV1Codec.PayloadLength];
        PkrgV1Codec.WriteRgbGetRequest(response, 88, 4);
        for (var index = 0; index < 12; index++)
            response[6 + index] = (byte)(index + 1);
        var colors = new Rgb24[4];

        PkrgV1Codec.ParseRgbGetResponse(response, 88, colors);

        Assert.Equal(new[]
        {
            new Rgb24(1, 2, 3), new Rgb24(4, 5, 6),
            new Rgb24(7, 8, 9), new Rgb24(10, 11, 12)
        }, colors);

        response[4] = 87;
        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseRgbGetResponse(response, 88, colors));
    }

    [Fact]
    public void RejectsRgbWriteAcknowledgementWithDifferentColorData()
    {
        var colors = new[] { new Rgb24(1, 2, 3), new Rgb24(4, 5, 6) };
        var response = new byte[PkrgV1Codec.PayloadLength];
        response[0] = 7;
        response[1] = 127;
        response[2] = 2;
        response[4] = 10;
        response[5] = 2;
        response[6] = 1;
        response[7] = 2;
        response[8] = 3;
        response[9] = 4;
        response[10] = 5;
        response[11] = 6;
        PkrgV1Codec.ParseRgbSetResponse(response, 10, colors);
        response[8] = 99;

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseRgbSetResponse(response, 10, colors));
    }

    [Fact]
    public void EncodesAndParsesOemBrightnessAndEffectValues()
    {
        var request = Enumerable.Repeat((byte)0xA5, PkrgV1Codec.PayloadLength).ToArray();

        PkrgV1Codec.WriteBrightnessGetRequest(request);
        Assert.Equal(new byte[] { 8, 3, 1 }, request[..3]);
        Assert.All(request[3..], value => Assert.Equal(0, value));
        request[3] = 9;
        Assert.Equal((byte)9, PkrgV1Codec.ParseBrightnessGetResponse(request));

        PkrgV1Codec.WriteBrightnessSetRequest(request, 9);
        Assert.Equal(new byte[] { 7, 3, 1, 9 }, request[..4]);
        Assert.All(request[4..], value => Assert.Equal(0, value));
        Assert.Equal((byte)9, PkrgV1Codec.ParseBrightnessSetResponse(request, 9));
        request[3] = 8;
        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseBrightnessSetResponse(request, 9));

        PkrgV1Codec.WriteEffectGetRequest(request);
        Assert.Equal(new byte[] { 8, 3, 2 }, request[..3]);
        request[3] = 18;
        Assert.Equal((byte)18, PkrgV1Codec.ParseEffectGetResponse(request));

        PkrgV1Codec.WriteEffectSetRequest(request, 6);
        Assert.Equal(new byte[] { 7, 3, 2, 6 }, request[..4]);
        Assert.Equal((byte)6, PkrgV1Codec.ParseEffectSetResponse(request, 6));
        request[3] = 5;
        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseEffectSetResponse(request, 6));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(92, 1)]
    [InlineData(91, 2)]
    [InlineData(0, 0)]
    [InlineData(0, 9)]
    public void RejectsRgbRangesOutsideOneTransfer(int startIndex, int count)
    {
        var request = new byte[PkrgV1Codec.PayloadLength];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PkrgV1Codec.WriteRgbGetRequest(request, startIndex, count));
    }

    [Fact]
    public void RejectsOutOfRangeOemSettings()
    {
        var request = new byte[PkrgV1Codec.PayloadLength];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PkrgV1Codec.WriteBrightnessSetRequest(request, 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PkrgV1Codec.WriteEffectSetRequest(request, 19));
    }

    [Fact]
    public void SdkExceptionsRetainOperationDeviceAndInnerException()
    {
        var device = new Crush80DeviceDescriptor("test", 0x320F, 0x5055, 0xFF60, 0x61, null, null);
        var cause = new InvalidOperationException("underlying");
        var exception = new ProtocolViolationException("bad response", "GetMode", device, cause);

        Assert.Equal("GetMode", exception.Operation);
        Assert.Same(device, exception.Device);
        Assert.Same(cause, exception.InnerException);
        Assert.IsAssignableFrom<Crush80SdkException>(exception);
    }
}
