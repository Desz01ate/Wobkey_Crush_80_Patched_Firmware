using System.Text.Json;
using Wobkey.Crush80.Protocol;

namespace Wobkey.Crush80.Sdk.Tests.Protocol;

public sealed class ConformanceCorpusTests
{
    [Fact]
    public void ParsesEachCompatibleCapabilityResponse()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pkrg-v1.json")));
        foreach (var item in document.RootElement.GetProperty("compatibleCapabilityResponses").EnumerateArray())
        {
            var response = new byte[PkrgV1Codec.PayloadLength];
            var prefix = item.GetProperty("responsePrefix").EnumerateArray()
                .Select(value => value.GetByte()).ToArray();
            prefix.CopyTo(response, 0);

            var capabilities = PkrgV1Codec.ParseCapabilitiesResponse(response);

            Assert.Equal(item.GetProperty("protocolVersion").GetByte(), capabilities.ProtocolVersion);
            Assert.Equal(92, capabilities.LedCount);
            Assert.Equal(8, capabilities.ChunkLimit);
            Assert.False(capabilities.Enabled);
            var ledMetadata = item.GetProperty("streamLedCount");
            var chunkMetadata = item.GetProperty("streamChunkCount");
            Assert.Equal(ledMetadata.ValueKind == JsonValueKind.Null ? null : ledMetadata.GetInt32(),
                capabilities.StreamLedCount);
            Assert.Equal(chunkMetadata.ValueKind == JsonValueKind.Null ? null : chunkMetadata.GetInt32(),
                capabilities.StreamChunkCount);
            Assert.Equal(item.GetProperty("protocolVersion").GetByte() == 2,
                capabilities.SupportsFrameStreaming);
        }
    }

    [Fact]
    public void CodecBuildsEverySupportedConformanceRequest()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pkrg-v1.json")));
        var root = document.RootElement;

        Assert.Equal(PkrgV1Codec.LedCount, root.GetProperty("ledCount").GetInt32());
        Assert.Equal(PkrgV1Codec.ChunkLimit, root.GetProperty("chunkLimit").GetInt32());
        var request = new byte[PkrgV1Codec.PayloadLength];
        foreach (var item in root.GetProperty("protocolCases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            request.AsSpan().Fill(0xA5);

            switch (name)
            {
                case "capabilities-disabled":
                    PkrgV1Codec.WriteCapabilitiesRequest(request);
                    break;
                case "read-default-mode":
                    PkrgV1Codec.WriteModeGetRequest(request);
                    break;
                case "enable-mode":
                    PkrgV1Codec.WriteModeSetRequest(request, true);
                    break;
                case "write-complete-eight-led-chunk":
                    var colors = Enumerable.Range(0, 8)
                        .Select(index => new Rgb24((byte)(index * 3), (byte)(index * 3 + 1), (byte)(index * 3 + 2)))
                        .ToArray();
                    PkrgV1Codec.WriteRgbSetRequest(request, 0, colors);
                    break;
                case "read-final-four-led-chunk":
                    PkrgV1Codec.WriteRgbGetRequest(request, 88, 4);
                    break;
                default:
                    if (item.GetProperty("expectedStatus").GetByte() == 0)
                        throw new InvalidOperationException($"No request writer covers valid corpus case '{name}'.");
                    continue;
            }

            var expectedRequest = item.GetProperty("request").EnumerateArray()
                .Select(value => value.GetByte()).ToArray();
            Assert.Equal(expectedRequest, request[..expectedRequest.Length].ToArray());
            Assert.All(request[expectedRequest.Length..].ToArray(), value => Assert.Equal(0, value));
        }
    }
}
