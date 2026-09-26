using Wobkey.Crush80.Sdk.Session;
using Wobkey.Crush80.Sdk.Transport;

namespace Wobkey.Crush80.Adapter;

internal static class AdapterSessionFactory
{
    internal static ValueTask<Crush80RgbSession> OpenAsync(
        IHidTransport transport,
        CancellationToken cancellationToken) =>
        Crush80RgbSession.OpenAsync(transport, cancellationToken: cancellationToken);
}
