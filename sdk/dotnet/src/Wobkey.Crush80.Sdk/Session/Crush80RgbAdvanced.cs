using Wobkey.Crush80.Protocol;

namespace Wobkey.Crush80;

/// <summary>Direct diagnostic and device-level RGB operations on a negotiated session.</summary>
public sealed class Crush80RgbAdvanced
{
    private readonly Crush80RgbSession _session;

    internal Crush80RgbAdvanced(Crush80RgbSession session) => _session = session;

    /// <summary>Captures the entire RGB buffer, mode, brightness, and effect as one logical operation.</summary>
    public ValueTask<RgbDeviceState> CaptureStateAsync(CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync("CaptureState", async (client, token) =>
        {
            var colors = new Rgb24[_session.Capabilities.LedCount];
            await client.ReadRangeAsync(0, colors, token).ConfigureAwait(false);
            var enabled = await client.GetEnabledAsync(token).ConfigureAwait(false);
            var brightness = await client.GetBrightnessAsync(token).ConfigureAwait(false);
            var effect = await client.GetEffectAsync(token).ConfigureAwait(false);
            return RgbDeviceState.FromCapturedFrame(colors, enabled, brightness, effect);
        }, cancellationToken);

    /// <summary>Restores a saved snapshot with the override disabled while its RGB buffer is written.</summary>
    public ValueTask RestoreStateAsync(RgbDeviceState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return _session.ExecuteAsync("RestoreState", async (client, token) =>
        {
            List<Exception>? failures = null;
            var disabled = false;
            try
            {
                await client.SetEnabledAsync(false, token).ConfigureAwait(false);
                disabled = true;
            }
            catch (FirmwareRejectedRequestException error)
            {
                (failures ??= []).Add(error);
            }

            var frameRestored = false;
            if (disabled)
            {
                try
                {
                    await client.WriteRangeAsync(0, state.Colors, token).ConfigureAwait(false);
                    frameRestored = true;
                }
                catch (FirmwareRejectedRequestException error)
                {
                    (failures ??= []).Add(error);
                }
            }

            try
            {
                await client.SetBrightnessAsync(state.Brightness, token).ConfigureAwait(false);
            }
            catch (FirmwareRejectedRequestException error)
            {
                (failures ??= []).Add(error);
            }

            try
            {
                await client.SetEffectAsync(state.Effect, token).ConfigureAwait(false);
            }
            catch (FirmwareRejectedRequestException error)
            {
                (failures ??= []).Add(error);
            }

            if (frameRestored)
            {
                try
                {
                    await client.SetEnabledAsync(state.Enabled, token).ConfigureAwait(false);
                }
                catch (FirmwareRejectedRequestException error)
                {
                    (failures ??= []).Add(error);
                }
            }

            if (failures is { Count: > 0 })
                throw new StateRestoreException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }, cancellationToken);
    }

    /// <summary>Reads exactly one full negotiated RGB frame.</summary>
    public ValueTask ReadFrameAsync(Memory<Rgb24> destination, CancellationToken cancellationToken = default)
    {
        ValidateFrame(destination.Length, nameof(destination));
        return _session.ExecuteAsync("ReadFrame", (client, token) =>
            client.ReadRangeAsync(0, destination, token), cancellationToken);
    }

    /// <summary>Writes exactly one full negotiated RGB frame.</summary>
    public ValueTask WriteFrameAsync(ReadOnlyMemory<Rgb24> colors, CancellationToken cancellationToken = default)
    {
        ValidateFrame(colors.Length, nameof(colors));
        return _session.ExecuteAsync("WriteFrame", (client, token) =>
            client.WriteRangeAsync(0, colors, token), cancellationToken);
    }

    /// <summary>Writes one or more adjacent RGB chunks within the negotiated frame.</summary>
    public ValueTask WriteRangeAsync(int startIndex, ReadOnlyMemory<Rgb24> colors, CancellationToken cancellationToken = default)
    {
        if (startIndex < 0 || startIndex >= _session.Capabilities.LedCount)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (colors.Length < 1 || colors.Length > _session.Capabilities.LedCount - startIndex)
            throw new ArgumentOutOfRangeException(nameof(colors));
        return _session.ExecuteAsync("WriteRange", (client, token) =>
            client.WriteRangeAsync(startIndex, colors, token), cancellationToken);
    }

    /// <summary>Reads whether the per-key RGB override is enabled.</summary>
    public ValueTask<bool> GetEnabledAsync(CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync("GetEnabled", static (client, token) =>
            client.GetEnabledAsync(token), cancellationToken);

    /// <summary>Enables or disables the per-key RGB override.</summary>
    public ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync("SetEnabled", (client, token) =>
            client.SetEnabledAsync(enabled, token), cancellationToken);

    /// <summary>Reads the OEM hardware brightness.</summary>
    public ValueTask<byte> GetBrightnessAsync(CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync("GetBrightness", static (client, token) =>
            client.GetBrightnessAsync(token), cancellationToken);

    /// <summary>Sets the OEM hardware brightness from zero through nine.</summary>
    public ValueTask SetBrightnessAsync(byte brightness, CancellationToken cancellationToken = default)
    {
        if (brightness > 9)
            throw new ArgumentOutOfRangeException(nameof(brightness));
        return _session.ExecuteAsync("SetBrightness", (client, token) =>
            client.SetBrightnessAsync(brightness, token), cancellationToken);
    }

    /// <summary>Reads the OEM effect identifier.</summary>
    public ValueTask<byte> GetEffectAsync(CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync("GetEffect", static (client, token) =>
            client.GetEffectAsync(token), cancellationToken);

    /// <summary>Sets the OEM effect identifier from zero through eighteen.</summary>
    public ValueTask SetEffectAsync(byte effect, CancellationToken cancellationToken = default)
    {
        if (effect > 18)
            throw new ArgumentOutOfRangeException(nameof(effect));
        return _session.ExecuteAsync("SetEffect", (client, token) =>
            client.SetEffectAsync(effect, token), cancellationToken);
    }

    private void ValidateFrame(int length, string parameterName)
    {
        if (length != _session.Capabilities.LedCount)
            throw new ArgumentException($"A full frame requires exactly {_session.Capabilities.LedCount} colors.", parameterName);
    }
}
