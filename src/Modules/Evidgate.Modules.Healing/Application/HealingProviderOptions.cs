using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Hosting;

namespace Evidgate.Modules.Healing.Application;

/// <summary>Which adapter answers a healing call. <see cref="None"/> spends nothing and is the fail-closed default.</summary>
internal enum HealingProviderKind
{
    /// <summary>No provider was selected (or none may run here), so no call is placed.</summary>
    None = 0,

    /// <summary>The deterministic offline adapter, registered only in Development.</summary>
    Mock = 1,

    /// <summary>The configured HTTP provider, reached with the configured key.</summary>
    Http = 2,
}

/// <summary>
/// The provider a deployment selected, bound from <c>EVIDGATE:Healing:Llm</c>. Every value is deployment configuration,
/// so a host that names a provider it may not run fails its boot instead of degrading silently.
/// </summary>
internal sealed class HealingProviderOptions
{
    /// <summary>The section the host binds (environment spelling <c>EVIDGATE__HEALING__LLM__*</c>).</summary>
    public const string SectionName = "EVIDGATE:Healing:Llm";

    /// <summary>
    /// <c>none</c>, <c>mock</c> or <c>http</c>, case-insensitive. Unset is allowed in Development only, where it means
    /// the mock; everywhere else the boot refuses it, because no real provider may be enabled by silence.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Absolute base address of the provider's API, required for <c>http</c>. The adapter posts a relative path under
    /// it, so it must end with <c>/</c>: a base without it would have its last segment replaced instead of extended.
    /// </summary>
    public string? BaseAddress { get; set; }

    /// <summary>
    /// The <em>name</em> of the secret the adapter presents, resolved through <see cref="ISecretProvider"/>; required for
    /// <c>http</c>. It is a name, never the key, so it may appear in configuration and in a start-up message.
    /// </summary>
    public string? ApiKeySecret { get; set; }

    /// <summary>
    /// The key itself, declared as a secret so the binder can never read it from configuration: the adapter's request
    /// handler resolves it through <see cref="ISecretProvider"/> on every call and keeps the value nowhere. Nothing
    /// assigns this member; it exists so the key's configuration slot is declared, and a test proves it stays null.
    /// </summary>
    public string? ApiKey { get; internal set; }

    /// <summary>
    /// The provider that actually runs, settled by the module's post-configuration from <see cref="Provider"/>. It is a
    /// field, not a bound property, so no deployment can state it and no configuration key exists for it.
    /// </summary>
    internal HealingProviderKind Kind;
}

/// <summary>The provider words a deployment may state, and the kind each one runs.</summary>
internal static class HealingProviderSelection
{
    /// <summary>No provider: the gateway refuses before any adapter is reached.</summary>
    internal const string None = "none";

    /// <summary>The offline adapter, refused outside Development.</summary>
    internal const string Mock = "mock";

    /// <summary>The configured HTTP provider.</summary>
    internal const string Http = "http";

    /// <summary>Parses a stated provider word; <see langword="false"/> for anything outside the three.</summary>
    /// <param name="provider">The configured value, with any surrounding whitespace.</param>
    /// <param name="kind">The kind the word names.</param>
    /// <returns>Whether <paramref name="provider"/> is one of <see cref="None"/>, <see cref="Mock"/> or <see cref="Http"/>.</returns>
    internal static bool TryParse(string? provider, out HealingProviderKind kind)
    {
        kind = HealingProviderKind.None;
        var value = provider?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals(Mock, StringComparison.OrdinalIgnoreCase))
        {
            kind = HealingProviderKind.Mock;
            return true;
        }

        if (value.Equals(Http, StringComparison.OrdinalIgnoreCase))
        {
            kind = HealingProviderKind.Http;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The kind a host runs with. Unset means the mock in Development and nothing anywhere else; an unrecognised word also
    /// means nothing, so reading these options without the boot validator still cannot enable a provider by accident.
    /// </summary>
    /// <param name="provider">The configured value, with any surrounding whitespace.</param>
    /// <param name="environment">The host environment that decides what an unset value means.</param>
    /// <returns>The effective provider kind.</returns>
    internal static HealingProviderKind Resolve(string? provider, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(provider))
        {
            return environment.IsDevelopment() ? HealingProviderKind.Mock : HealingProviderKind.None;
        }

        return TryParse(provider, out var kind) ? kind : HealingProviderKind.None;
    }
}

/// <summary>The configuration keys a start-up refusal names, so the message and the matrix cannot drift apart.</summary>
internal static class HealingProviderKeys
{
    /// <summary>The provider selection.</summary>
    internal const string Provider = "EVIDGATE__HEALING__LLM__PROVIDER";

    /// <summary>The provider's base address.</summary>
    internal const string BaseAddress = "EVIDGATE__HEALING__LLM__BASEADDRESS";

    /// <summary>The name of the secret holding the provider's key.</summary>
    internal const string ApiKeySecret = "EVIDGATE__HEALING__LLM__APIKEYSECRET";
}
