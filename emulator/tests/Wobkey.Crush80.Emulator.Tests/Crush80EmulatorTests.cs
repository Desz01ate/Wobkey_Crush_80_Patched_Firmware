using Xunit;
using System.Net;
using System.Text.Json;
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;

namespace Wobkey.Crush80.Emulator.Tests;

public sealed class Crush80EmulatorTests
{
    [Fact]
    public async Task SdkLeaseUpdatesVisibleStateAndRestoresInitialState()
    {
        var transport = new Crush80EmulatedTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        Assert.Equal(2, session.Capabilities.ProtocolVersion);

        var initialFrame = new Rgb24[92];
        await using var lease = await session.AcquireControlAsync(initialFrame);
        var frame = new Rgb24[92];
        frame[0] = new Rgb24(255, 0, 0);
        frame[1] = new Rgb24(0, 255, 0);
        frame[91] = new Rgb24(0, 0, 255);
        await lease.WriteFrameAsync(frame);

        var active = transport.CaptureState();
        Assert.True(active.Connected);
        Assert.True(active.Enabled);
        Assert.Equal(9, active.Brightness);
        Assert.Equal(6, active.Effect);
        Assert.Equal(frame, active.Colors.ToArray());

        await lease.RestoreAsync();
        var restored = transport.CaptureState();
        Assert.False(restored.Enabled);
        Assert.Equal(9, restored.Brightness);
        Assert.Equal(7, restored.Effect);
        Assert.All(restored.Colors.ToArray(), color => Assert.Equal(default, color));
    }

    [Fact]
    public async Task BrowserApiPublishesAdapterFrameInFirmwareSlotOrder()
    {
        await using var emulator = await Crush80Emulator.StartAsync();
        using var client = new HttpClient { BaseAddress = emulator.VisualizerUri };

        using (var layout = JsonDocument.Parse(await client.GetStringAsync("api/layout")))
        {
            var root = layout.RootElement;
            Assert.Equal(37, root.GetProperty("width").GetInt32());
            Assert.Equal(12, root.GetProperty("height").GetInt32());
            var slots = root.GetProperty("slots");
            Assert.Equal(92, slots.GetArrayLength());
            Assert.Equal("Esc", slots[0].GetProperty("key").GetString());
            Assert.Equal("RightArrow", slots[91].GetProperty("key").GetString());
        }

        await using var keyboard = await Crush80Keyboard.OpenAsync(emulator.Transport);
        keyboard.Grid.SetAll(default);
        keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
        keyboard.Grid.SetKey(Crush80Key.F1, new Rgb24(0, 255, 0));
        keyboard.Grid.SetKey(Crush80Key.CapsLock, new Rgb24(0, 0, 255));
        await keyboard.ApplyAsync();

        using var state = JsonDocument.Parse(await client.GetStringAsync("api/state?after=-1"));
        var rootState = state.RootElement;
        Assert.True(rootState.GetProperty("connected").GetBoolean());
        Assert.True(rootState.GetProperty("enabled").GetBoolean());
        Assert.Equal(6, rootState.GetProperty("effect").GetByte());
        var colors = rootState.GetProperty("colors");
        Assert.Equal(276, colors.GetArrayLength());
        Assert.Equal(new byte[] { 255, 0, 0 }, ReadColor(colors, 0));
        Assert.Equal(new byte[] { 0, 255, 0 }, ReadColor(colors, 1));
        Assert.Equal(new byte[] { 0, 0, 255 }, ReadColor(colors, 51));
        Assert.Equal(new byte[] { 0, 0, 255 }, ReadColor(colors, 52));
        Assert.Equal(new byte[] { 0, 0, 0 }, ReadColor(colors, 53));

        var unchanged = await client.GetAsync($"api/state?after={rootState.GetProperty("version").GetInt64()}");
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
    }

    private static byte[] ReadColor(JsonElement colors, int index)
    {
        var offset = index * 3;
        return
        [
            colors[offset].GetByte(),
            colors[offset + 1].GetByte(),
            colors[offset + 2].GetByte()
        ];
    }
}
