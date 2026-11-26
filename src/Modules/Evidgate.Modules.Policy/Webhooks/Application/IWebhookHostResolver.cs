using System.Net;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>Name resolution behind <see cref="WebhookUrlGuard"/>, injectable so tests never touch DNS.</summary>
internal interface IWebhookHostResolver
{
    /// <summary>All addresses the host currently resolves to.</summary>
    /// <param name="host">A DNS name without a trailing dot.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}
