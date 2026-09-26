using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80.Protocol;

// The owning session serializes calls; request and response buffers are intentionally shared.
internal sealed class PkrgV1Client
{
    private readonly IHidTransport _transport;
    private readonly TimeSpan _responseTimeout;
    private readonly byte[] _request = new byte[PkrgV1Codec.PayloadLength];
    private readonly byte[] _response = new byte[PkrgV1Codec.PayloadLength];

    internal PkrgV1Client(IHidTransport transport, TimeSpan responseTimeout)
    {
        _transport = transport;
        _responseTimeout = responseTimeout;
    }

    internal async ValueTask<PerKeyRgbCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteCapabilitiesRequest(_request);
        await ExchangeAsync("GetCapabilities", cancellationToken).ConfigureAwait(false);
        return PkrgV1Codec.ParseCapabilitiesResponse(_response);
    }

    internal async ValueTask<bool> GetEnabledAsync(CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteModeGetRequest(_request);
        await ExchangeAsync("GetEnabled", cancellationToken).ConfigureAwait(false);
        return PkrgV1Codec.ParseModeGetResponse(_response);
    }

    internal async ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteModeSetRequest(_request, enabled);
        await ExchangeAsync("SetEnabled", cancellationToken).ConfigureAwait(false);
        PkrgV1Codec.ParseModeSetResponse(_response, enabled);
    }

    internal async ValueTask ReadRangeAsync(int startIndex, Memory<Rgb24> destination, CancellationToken cancellationToken)
    {
        ValidateRange(startIndex, destination.Length);
        for (var offset = 0; offset < destination.Length;)
        {
            var count = Math.Min(PkrgV1Codec.ChunkLimit, destination.Length - offset);
            PkrgV1Codec.WriteRgbGetRequest(_request, startIndex + offset, count);
            await ExchangeAsync("ReadRgb", cancellationToken).ConfigureAwait(false);
            PkrgV1Codec.ParseRgbGetResponse(_response, startIndex + offset, destination.Span.Slice(offset, count));
            offset += count;
        }
    }

    internal async ValueTask WriteRangeAsync(int startIndex, ReadOnlyMemory<Rgb24> colors, CancellationToken cancellationToken)
    {
        ValidateRange(startIndex, colors.Length);
        for (var offset = 0; offset < colors.Length;)
        {
            var count = Math.Min(PkrgV1Codec.ChunkLimit, colors.Length - offset);
            PkrgV1Codec.WriteRgbSetRequest(_request, startIndex + offset, colors.Span.Slice(offset, count));
            await ExchangeAsync("WriteRgb", cancellationToken).ConfigureAwait(false);
            PkrgV1Codec.ParseRgbSetResponse(_response, startIndex + offset, colors.Span.Slice(offset, count));
            offset += count;
        }
    }

    internal async ValueTask<byte> GetBrightnessAsync(CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteBrightnessGetRequest(_request);
        await ExchangeAsync("GetBrightness", cancellationToken).ConfigureAwait(false);
        return PkrgV1Codec.ParseBrightnessGetResponse(_response);
    }

    internal async ValueTask SetBrightnessAsync(byte brightness, CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteBrightnessSetRequest(_request, brightness);
        await ExchangeAsync("SetBrightness", cancellationToken).ConfigureAwait(false);
        PkrgV1Codec.ParseBrightnessSetResponse(_response, brightness);
    }

    internal async ValueTask<byte> GetEffectAsync(CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteEffectGetRequest(_request);
        await ExchangeAsync("GetEffect", cancellationToken).ConfigureAwait(false);
        return PkrgV1Codec.ParseEffectGetResponse(_response);
    }

    internal async ValueTask SetEffectAsync(byte effect, CancellationToken cancellationToken)
    {
        PkrgV1Codec.WriteEffectSetRequest(_request, effect);
        await ExchangeAsync("SetEffect", cancellationToken).ConfigureAwait(false);
        PkrgV1Codec.ParseEffectSetResponse(_response, effect);
    }

    private async ValueTask ExchangeAsync(string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A cancelled caller must not strand a reply that could match the next request.
        await _transport.WriteAsync(_request, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _transport.ReadAsync(_response, _responseTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new ProtocolViolationException(
                "The device did not respond to the PKRG request in time.",
                operation,
                _transport.Device,
                timeout);
        }
    }

    private static void ValidateRange(int startIndex, int count)
    {
        if (startIndex < 0 || startIndex >= PkrgV1Codec.LedCount)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (count < 1 || count > PkrgV1Codec.LedCount - startIndex)
            throw new ArgumentOutOfRangeException(nameof(count));
    }
}
