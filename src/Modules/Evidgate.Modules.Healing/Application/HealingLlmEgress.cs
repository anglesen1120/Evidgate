using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The egress allowlist of the healing adapter: the only endpoint a request may reach is the configured base address,
/// and outside Development that address may not be an internal one.
/// </summary>
internal static class HealingLlmEgress
{
    /// <summary>Whether <paramref name="uri"/> is the configured endpoint or a path under it (same scheme, host and port).</summary>
    /// <param name="baseAddress">The validated base address.</param>
    /// <param name="uri">The request target.</param>
    internal static bool IsAllowed(Uri baseAddress, Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.UserInfo.Length == 0
        && uri.Scheme.Equals(baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(baseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == baseAddress.Port
        && uri.AbsolutePath.StartsWith(baseAddress.AbsolutePath, StringComparison.Ordinal);

    /// <summary>
    /// Whether the host of <paramref name="uri"/> is one a public provider cannot be: an unqualified name, or an IP literal
    /// in a loopback, private, link-local, unique-local or unspecified range. Name resolution is not judged here.
    /// </summary>
    /// <param name="uri">The base address.</param>
    internal static bool IsInternalHost(Uri uri) => PublicNetworkAddress.IsBlockedHost(uri);
}

/// <summary>Refuses a request whose target is not the configured provider endpoint, before any key is resolved for it.</summary>
internal sealed class HealingLlmEgressHandler(HealingProviderOptions options) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress)
            || !HealingLlmEgress.IsAllowed(baseAddress, request.RequestUri))
        {
            // The message names the configuration key only: neither the target nor the payload may reach a log.
            throw new HealingLlmNotSentException(
                $"the request target is not under {HealingProviderKeys.BaseAddress}, so it may not leave");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
