using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wobkey.Crush80.Adapter;

namespace Wobkey.Crush80.Emulator;

internal sealed class Crush80Visualizer : IAsyncDisposable
{
    private static readonly byte[] IndexPage = LoadIndexPage();
    private static readonly LayoutResponse Layout = CreateLayout();
    private readonly WebApplication _application;
    private int _disposed;

    private Crush80Visualizer(WebApplication application, Uri uri)
    {
        _application = application;
        Uri = uri;
    }

    internal Uri Uri { get; }

    internal static async ValueTask<Crush80Visualizer> StartAsync(
        Crush80EmulatedTransport transport,
        int port,
        CancellationToken cancellationToken)
    {
        if ((uint)port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535.");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Crush80Visualizer).Assembly.GetName().Name,
            Args = []
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));

        var application = builder.Build();
        application.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(IndexPage, "text/html; charset=utf-8");
        });
        application.MapGet("/api/layout", () => Results.Json(Layout));
        application.MapGet("/api/state", (long? after) => GetState(transport, after));

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault()
                ?? throw new InvalidOperationException("The visualizer did not publish a listening address.");
            return new Crush80Visualizer(application, new Uri(address));
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IResult GetState(Crush80EmulatedTransport transport, long? after)
    {
        if (after == transport.Version)
            return Results.NoContent();

        var state = transport.CaptureState();
        if (after == state.Version)
            return Results.NoContent();

        var colors = new int[state.Colors.Length * 3];
        var source = state.Colors.Span;
        for (var index = 0; index < source.Length; index++)
        {
            var offset = index * 3;
            colors[offset] = source[index].Red;
            colors[offset + 1] = source[index].Green;
            colors[offset + 2] = source[index].Blue;
        }

        return Results.Json(new StateResponse(
            state.Version,
            state.Connected,
            state.ClientConnected,
            state.Enabled,
            state.Brightness,
            state.Effect,
            colors));
    }

    private static LayoutResponse CreateLayout()
    {
        var slots = new LayoutSlot[Crush80Layout.LedSlots.Count];
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = Crush80Layout.LedSlots[index];
            slots[index] = new LayoutSlot(index, slot.Key.ToString(), Label(slot.Key), slot.X, slot.Y);
        }

        return new LayoutResponse(Crush80Layout.Width, Crush80Layout.Height, slots);
    }

    private static string Label(Crush80Key key)
    {
        var name = key.ToString();
        if (name.StartsWith("Digit", StringComparison.Ordinal))
            return name[5..];
        return key switch
        {
            Crush80Key.AudioMute => "Mute",
            Crush80Key.PrintScreen => "PrtSc",
            Crush80Key.ScrollLock => "ScrLk",
            Crush80Key.PauseBreak => "Pause",
            Crush80Key.GraveAccent => "`",
            Crush80Key.Minus => "-",
            Crush80Key.Equals => "=",
            Crush80Key.Backspace => "Bksp",
            Crush80Key.Insert => "Ins",
            Crush80Key.Delete => "Del",
            Crush80Key.PageUp => "PgUp",
            Crush80Key.PageDown => "PgDn",
            Crush80Key.CapsLock => "Caps",
            Crush80Key.Enter => "Ent",
            Crush80Key.LeftBracket => "[",
            Crush80Key.RightBracket => "]",
            Crush80Key.Backslash => "\\",
            Crush80Key.Semicolon => ";",
            Crush80Key.Apostrophe => "'",
            Crush80Key.IsoHash => "#",
            Crush80Key.Comma => ",",
            Crush80Key.Period => ".",
            Crush80Key.Slash => "/",
            Crush80Key.LeftCtrl => "LCtrl",
            Crush80Key.LeftWin => "LWin",
            Crush80Key.LeftAlt => "LAlt",
            Crush80Key.RightAlt => "RAlt",
            Crush80Key.RightWin => "RWin",
            Crush80Key.RightCtrl => "RCtrl",
            Crush80Key.LeftShift => "LShift",
            Crush80Key.RightShift => "RShift",
            Crush80Key.LeftArrow => "Left",
            Crush80Key.DownArrow => "Down",
            Crush80Key.RightArrow => "Right",
            Crush80Key.UpArrow => "Up",
            _ => name
        };
    }

    private static byte[] LoadIndexPage()
    {
        const string resourceName = "Wobkey.Crush80.Emulator.Web.index.html";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded visualizer resource {resourceName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record LayoutResponse(int Width, int Height, LayoutSlot[] Slots);
    private sealed record LayoutSlot(int Index, string Key, string Label, byte X, byte Y);
    private sealed record StateResponse(
        long Version,
        bool Connected,
        bool ClientConnected,
        bool Enabled,
        byte Brightness,
        byte Effect,
        int[] Colors);
}
