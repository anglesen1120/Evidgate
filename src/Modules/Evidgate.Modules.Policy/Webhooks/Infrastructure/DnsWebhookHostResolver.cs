using System.Net;
using System.Net.Sockets;
using Evidgate.Modules.Policy.Webhooks.Application;

namespace Evidgate.Modules.Policy.Webhooks.Infrastructure;

/// <summary>Production resolver: the system resolver, every returned address.</summary>
internal sealed class DnsWebhookHostResolver : IWebhookHostResolver
{
    private const int ResolutionTimeoutMilliseconds = 3000;

    /// <inheritdoc />
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ResolutionTimeoutMilliseconds);

        try
        {
            // A native resolver may finish an in-flight lookup after cancellation; bound our wait as well.
            return await Dns.GetHostAddressesAsync(host, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SocketException((int)SocketError.TimedOut);
        }
    }
}
