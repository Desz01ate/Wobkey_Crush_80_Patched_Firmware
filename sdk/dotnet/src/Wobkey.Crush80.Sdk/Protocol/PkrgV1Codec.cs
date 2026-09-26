namespace Wobkey.Crush80.Protocol;

// Optimized firmware a88d885 (docs/user/PER-KEY-RGB.md, "Frame streaming (PKRG v2)")
// retains v1 mode/chunk GET/SET layouts, with chunk accesses on its active buffer.
// This codec intentionally uses only that sequential common subset, not v2 operations 3/4.
internal static class PkrgV1Codec
{
    internal const int PayloadLength = 32;
    internal const byte Channel = 0x7F;
    internal const int LedCount = 92;
    internal const int ChunkLimit = 8;

    private const byte GetCommand = 8;
    private const byte SetCommand = 7;
    private const byte ModeOperation = 1;
    private const byte RgbOperation = 2;
    private const byte OemChannel = 3;
    private const byte BrightnessId = 1;
    private const byte EffectId = 2;
    private const byte MaximumBrightness = 9;
    private const byte MaximumEffect = 18;

    internal static void WriteCapabilitiesRequest(Span<byte> destination)
    {
        Start(destination, GetCommand, Channel, operation: 0);
    }

    internal static PerKeyRgbCapabilities ParseCapabilitiesResponse(ReadOnlySpan<byte> response, Crush80DeviceDescriptor? device = null)
    {
        ValidateResponseHeader(response, GetCommand, Channel, 0, "GetCapabilities", device);
        if (response[3] != 0)
            throw new IncompatibleFirmwareException(
                $"The firmware rejected PKRG capabilities (status {response[3]}).",
                operation: "GetCapabilities", device: device);

        var signatureMatches = response[4] == (byte)'P' && response[5] == (byte)'K' &&
            response[6] == (byte)'R' && response[7] == (byte)'G';
        var version = response[8];
        var streamLeds = response[12];
        var streamChunks = response[13];
        if (!signatureMatches || (version != 1 && version != 2) ||
            response[9] != LedCount || response[10] != ChunkLimit || response[11] > 1 ||
            (version == 2 && (streamLeds != 9 || streamChunks != 11)))
        {
            throw new IncompatibleFirmwareException(
                $"Expected PKRG version 1 or version 2 (92 LEDs, chunk limit 8; v2 stream 9 LEDs x 11 fragments); " +
                $"received signature={(signatureMatches ? "PKRG" : "invalid")}, version={version}, " +
                $"LEDs={response[9]}, chunk limit={response[10]}, enabled={response[11]}, " +
                $"stream LEDs={streamLeds}, stream fragments={streamChunks}.",
                operation: "GetCapabilities", device: device);
        }

        return new PerKeyRgbCapabilities(version, LedCount, ChunkLimit, response[11] == 1)
        {
            StreamLedCount = version == 2 ? streamLeds : null,
            StreamChunkCount = version == 2 ? streamChunks : null
        };
    }

    internal static void WriteModeGetRequest(Span<byte> destination)
    {
        Start(destination, GetCommand, Channel, ModeOperation);
    }

    internal static bool ParseModeGetResponse(ReadOnlySpan<byte> response, Crush80DeviceDescriptor? device = null)
    {
        ValidatePkrgResponse(response, GetCommand, Channel, ModeOperation, "GetEnabled", device);
        var enabled = response[4];
        if (enabled > 1)
            throw InvalidResponse("The firmware returned an invalid enabled flag.", "GetEnabled", device);

        return enabled == 1;
    }

    internal static void WriteModeSetRequest(Span<byte> destination, bool enabled)
    {
        Start(destination, SetCommand, Channel, ModeOperation);
        destination[4] = enabled ? (byte)1 : (byte)0;
    }

    internal static void ParseModeSetResponse(ReadOnlySpan<byte> response, bool expectedEnabled, Crush80DeviceDescriptor? device = null)
    {
        ValidatePkrgResponse(response, SetCommand, Channel, ModeOperation, "SetEnabled", device);
        var expected = expectedEnabled ? (byte)1 : (byte)0;
        if (response[4] > 1 || response[4] != expected)
            throw InvalidResponse("The mode acknowledgement did not match the requested enabled state.", "SetEnabled", device);
    }

    internal static void WriteRgbGetRequest(Span<byte> destination, int startIndex, int count)
    {
        ValidateRange(startIndex, count);
        if (count > ChunkLimit)
            throw new ArgumentOutOfRangeException(nameof(count), $"A PKRG transfer cannot exceed {ChunkLimit} LEDs.");

        Start(destination, GetCommand, Channel, RgbOperation);
        destination[4] = checked((byte)startIndex);
        destination[5] = checked((byte)count);
    }

    internal static void ParseRgbGetResponse(
        ReadOnlySpan<byte> response,
        int expectedStartIndex,
        Span<Rgb24> destination,
        Crush80DeviceDescriptor? device = null)
    {
        ValidateRange(expectedStartIndex, destination.Length);
        if (destination.Length > ChunkLimit)
            throw new ArgumentOutOfRangeException(nameof(destination), $"A PKRG transfer cannot exceed {ChunkLimit} LEDs.");

        ValidatePkrgResponse(response, GetCommand, Channel, RgbOperation, "ReadRgb", device);
        ValidateRgbEcho(response, expectedStartIndex, destination.Length, "ReadRgb", device);

        for (var index = 0; index < destination.Length; index++)
        {
            var offset = 6 + index * 3;
            destination[index] = new Rgb24(response[offset], response[offset + 1], response[offset + 2]);
        }
    }

    internal static void WriteRgbSetRequest(
        Span<byte> destination,
        int startIndex,
        ReadOnlySpan<Rgb24> colors)
    {
        ValidateRange(startIndex, colors.Length);
        if (colors.Length > ChunkLimit)
            throw new ArgumentOutOfRangeException(nameof(colors), $"A PKRG transfer cannot exceed {ChunkLimit} LEDs.");

        Start(destination, SetCommand, Channel, RgbOperation);
        destination[4] = checked((byte)startIndex);
        destination[5] = checked((byte)colors.Length);
        for (var index = 0; index < colors.Length; index++)
        {
            var offset = 6 + index * 3;
            destination[offset] = colors[index].Red;
            destination[offset + 1] = colors[index].Green;
            destination[offset + 2] = colors[index].Blue;
        }
    }

    internal static void ParseRgbSetResponse(
        ReadOnlySpan<byte> response,
        int expectedStartIndex,
        ReadOnlySpan<Rgb24> expectedColors,
        Crush80DeviceDescriptor? device = null)
    {
        ValidateRange(expectedStartIndex, expectedColors.Length);
        if (expectedColors.Length > ChunkLimit)
            throw new ArgumentOutOfRangeException(nameof(expectedColors), $"A PKRG transfer cannot exceed {ChunkLimit} LEDs.");

        ValidatePkrgResponse(response, SetCommand, Channel, RgbOperation, "WriteRgb", device);
        ValidateRgbEcho(response, expectedStartIndex, expectedColors.Length, "WriteRgb", device);

        for (var index = 0; index < expectedColors.Length; index++)
        {
            var offset = 6 + index * 3;
            var color = expectedColors[index];
            if (response[offset] != color.Red || response[offset + 1] != color.Green || response[offset + 2] != color.Blue)
                throw InvalidResponse("The RGB acknowledgement did not match the requested colors.", "WriteRgb", device);
        }
    }

    internal static void WriteBrightnessGetRequest(Span<byte> destination)
    {
        Start(destination, GetCommand, OemChannel, BrightnessId);
    }

    internal static byte ParseBrightnessGetResponse(ReadOnlySpan<byte> response, Crush80DeviceDescriptor? device = null)
    {
        ValidateOemResponse(response, GetCommand, BrightnessId, "GetBrightness", device);
        var brightness = response[3];
        if (brightness > MaximumBrightness)
            throw InvalidResponse("The firmware returned an invalid hardware brightness.", "GetBrightness", device);

        return brightness;
    }

    internal static void WriteBrightnessSetRequest(Span<byte> destination, byte brightness)
    {
        ValidateBrightness(brightness);
        Start(destination, SetCommand, OemChannel, BrightnessId);
        destination[3] = brightness;
    }

    internal static byte ParseBrightnessSetResponse(ReadOnlySpan<byte> response, byte expectedBrightness, Crush80DeviceDescriptor? device = null)
    {
        ValidateBrightness(expectedBrightness);
        ValidateOemResponse(response, SetCommand, BrightnessId, "SetBrightness", device);
        if (response[3] != expectedBrightness)
            throw InvalidResponse("The brightness acknowledgement did not match the requested value.", "SetBrightness", device);

        return response[3];
    }

    internal static void WriteEffectGetRequest(Span<byte> destination)
    {
        Start(destination, GetCommand, OemChannel, EffectId);
    }

    internal static byte ParseEffectGetResponse(ReadOnlySpan<byte> response, Crush80DeviceDescriptor? device = null)
    {
        ValidateOemResponse(response, GetCommand, EffectId, "GetEffect", device);
        var effect = response[3];
        if (effect > MaximumEffect)
            throw InvalidResponse("The firmware returned an invalid effect identifier.", "GetEffect", device);

        return effect;
    }

    internal static void WriteEffectSetRequest(Span<byte> destination, byte effect)
    {
        ValidateEffect(effect);
        Start(destination, SetCommand, OemChannel, EffectId);
        destination[3] = effect;
    }

    internal static byte ParseEffectSetResponse(ReadOnlySpan<byte> response, byte expectedEffect, Crush80DeviceDescriptor? device = null)
    {
        ValidateEffect(expectedEffect);
        ValidateOemResponse(response, SetCommand, EffectId, "SetEffect", device);
        if (response[3] != expectedEffect)
            throw InvalidResponse("The effect acknowledgement did not match the requested value.", "SetEffect", device);

        return response[3];
    }

    private static void Start(Span<byte> destination, byte command, byte channel, byte operation)
    {
        if (destination.Length != PayloadLength)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(destination));

        destination.Clear();
        destination[0] = command;
        destination[1] = channel;
        destination[2] = operation;
    }

    private static void ValidateRange(int startIndex, int count)
    {
        if (startIndex < 0 || startIndex >= LedCount)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (count < 1 || count > LedCount - startIndex)
            throw new ArgumentOutOfRangeException(nameof(count));
    }

    private static void ValidateBrightness(byte brightness)
    {
        if (brightness > MaximumBrightness)
            throw new ArgumentOutOfRangeException(nameof(brightness), "Hardware brightness must be between 0 and 9.");
    }

    private static void ValidateEffect(byte effect)
    {
        if (effect > MaximumEffect)
            throw new ArgumentOutOfRangeException(nameof(effect), "The OEM effect identifier must be between 0 and 18.");
    }

    private static void ValidatePkrgResponse(
        ReadOnlySpan<byte> response,
        byte expectedCommand,
        byte expectedChannel,
        byte expectedOperation,
        string operation,
        Crush80DeviceDescriptor? device)
    {
        ValidateResponseHeader(response, expectedCommand, expectedChannel, expectedOperation, operation, device);
        // v2 can also return status 5 for mode/chunk SET while a commit/ACK is pending;
        // all nonzero PKRG statuses, including v2's 4/5, remain firmware rejections.
        if (response[3] != 0)
            throw new FirmwareRejectedRequestException(response[3], operation, device);
    }

    private static void ValidateOemResponse(
        ReadOnlySpan<byte> response,
        byte expectedCommand,
        byte expectedOperation,
        string operation,
        Crush80DeviceDescriptor? device)
    {
        ValidateResponseHeader(response, expectedCommand, OemChannel, expectedOperation, operation, device);
    }

    private static void ValidateResponseHeader(
        ReadOnlySpan<byte> response,
        byte expectedCommand,
        byte expectedChannel,
        byte expectedOperation,
        string operation,
        Crush80DeviceDescriptor? device)
    {
        if (response.Length != PayloadLength)
            throw InvalidResponse("A VIA response must contain exactly 32 bytes.", operation, device);

        if (response[0] != expectedCommand || response[1] != expectedChannel || response[2] != expectedOperation)
            throw InvalidResponse("The VIA response command, channel, or operation did not match the request.", operation, device);
    }

    private static void ValidateRgbEcho(ReadOnlySpan<byte> response, int startIndex, int count, string operation, Crush80DeviceDescriptor? device)
    {
        if (response[4] != startIndex || response[5] != count)
            throw InvalidResponse("The RGB response returned a different LED range.", operation, device);
    }

    private static ProtocolViolationException InvalidResponse(string message, string operation, Crush80DeviceDescriptor? device) =>
        new(message, operation, device);
}
