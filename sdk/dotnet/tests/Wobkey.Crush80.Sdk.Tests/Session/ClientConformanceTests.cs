using System.Text.Json;
using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class ClientConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pkrg-v1.json")));
        foreach (var item in document.RootElement.GetProperty("clientCases").EnumerateArray())
        {
            yield return
            [
                item.GetProperty("name").GetString()!,
                item.GetProperty("request").EnumerateArray().Select(value => value.GetByte()).ToArray(),
                item.TryGetProperty("responsePrefix", out var prefix)
                    ? prefix.EnumerateArray().Select(value => value.GetByte()).ToArray()
                    : null!,
                item.TryGetProperty("response", out var response) ? response.GetString()! : null!,
                item.GetProperty("expectedException").GetString()!,
                item.GetProperty("faultsSession").GetBoolean()
            ];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExecutesSharedClientFailureCases(
        string name, byte[] request, byte[]? responsePrefix, string? response,
        string expectedException, bool faultsSession)
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        if (responsePrefix is not null && response is null)
        {
            var reply = new byte[32];
            responsePrefix.CopyTo(reply, 0);
            transport.NextResponse = reply;
        }
        else if (response == "timeout" && responsePrefix is null)
            transport.TimeoutNextRead = true;
        else
            throw new InvalidDataException($"Unsupported client response in corpus case {name}.");

        Func<Task> operation = request switch
        {
            [7, 127, 1, _, _] => async () => await session.Advanced.SetEnabledAsync(request[4] != 0),
            [8, 127, 1] => async () => await session.Advanced.GetEnabledAsync(),
            _ => throw new InvalidDataException($"Unsupported client request in corpus case {name}.")
        };

        var error = await Record.ExceptionAsync(operation);
        var expectedRequest = new byte[32];
        request.CopyTo(expectedRequest, 0);
        Assert.Equal(expectedRequest, transport.LastRequest.ToArray());
        Assert.Equal(expectedException, error?.GetType().Name);
        if (faultsSession)
            await Assert.ThrowsAsync<SessionFaultedException>(async () =>
                await session.Advanced.GetBrightnessAsync());
        else
            await session.Advanced.GetBrightnessAsync();
    }
}
