using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// Presents the provider's key on every request, read from the secret provider at send time. The value never reaches the
/// options snapshot, a log line or an exception message: only the name of the secret is configuration.
/// </summary>
internal sealed class HealingLlmApiKeyHandler(ISecretProvider secrets, HealingProviderOptions options) : DelegatingHandler
{
    /// <summary>The scheme the key is presented under.</summary>
    private const string Scheme = "Bearer";

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var secret = options.ApiKeySecret?.Trim();

        if (string.IsNullOrEmpty(secret))
        {
            // Unreachable once the boot validator has run; refusing here keeps a composition that skipped it fail-closed.
            throw new InvalidOperationException(
                $"{HealingProviderKeys.ApiKeySecret} is unset, so no request may leave without a key");
        }

        var key = await secrets.GetSecretAsync(secret, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {
            // Nothing is sent without a key: an unauthenticated request would leave the configured endpoint and be
            // recorded as a provider attempt. The message names the configuration key, never the secret.
            throw new InvalidOperationException(
                $"the secret named in {HealingProviderKeys.ApiKeySecret} resolved to no value, so no request may leave");
        }

        // A header value cannot carry CR, LF or NUL: the serializer would throw a FormatException no caller maps, so the
        // refusal happens here, names the configuration key and keeps the value out of the message.
        if (key.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            throw new InvalidOperationException(
                $"the secret named in {HealingProviderKeys.ApiKeySecret} carries a control character, so no request may leave");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, key);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
