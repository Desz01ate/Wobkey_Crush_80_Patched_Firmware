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
            yield return
            [
                item.GetProperty("name").GetString()!,
                item.GetProperty("expectedException").GetString()!,
                item.GetProperty("faultsSession").GetBoolean()
            ];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExecutesSharedClientFailureCases(string name, string expectedException, bool faultsSession)
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        Func<Task> operation = name switch
        {
            "mismatched-operation-faults-session" => async () =>
            {
                transport.MalformOperationOnce = "SetEnabled";
                await session.Advanced.SetEnabledAsync(true);
            },
            "timeout-faults-session" => async () =>
            {
                transport.TimeoutNextRead = true;
                await session.Advanced.GetEnabledAsync();
            },
            _ => throw new InvalidDataException($"Unknown conformance case: {name}")
        };

        var error = await Record.ExceptionAsync(operation);
        Assert.Equal(expectedException, error?.GetType().Name);
        if (faultsSession)
            await Assert.ThrowsAsync<SessionFaultedException>(async () =>
                await session.Advanced.GetBrightnessAsync());
    }
}
