using System.Net;
using Evidgate.Modules.Policy.Webhooks.Application;

namespace Evidgate.Modules.Policy.Webhooks.Infrastructure;

/// <summary>Production resolver: the system resolver, every returned address.</summary>
internal sealed class DnsWebhookHostResolver : IWebhookHostResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}
