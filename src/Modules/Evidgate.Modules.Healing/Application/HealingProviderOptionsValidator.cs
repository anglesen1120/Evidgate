using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// Fails the boot of a host that selected a provider it cannot run. Every refusal names the configuration key and never
/// the configured value, so a start-up message can be pasted into a ticket without carrying a credential name around.
/// </summary>
internal sealed class HealingProviderOptionsValidator(IHostEnvironment environment) : IValidateOptions<HealingProviderOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HealingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var development = environment.IsDevelopment();
        var stated = options.Provider?.Trim();
        // The checks read the kind the stated word resolves to, not the settled field, so validation cannot be skipped by
        // a composition that reads the options before the module's post-configuration ran.
        var kind = HealingProviderSelection.Resolve(stated, environment);

        if (string.IsNullOrEmpty(stated))
        {
            if (!development)
            {
                problems.Add(
                    $"{HealingProviderKeys.Provider}: must be set outside Development; use '{HealingProviderSelection.None}' when "
                    + "healing may not call a provider");
            }
        }
        else if (!HealingProviderSelection.TryParse(stated, out _))
        {
            problems.Add(
                $"{HealingProviderKeys.Provider}: is not a provider; use '{HealingProviderSelection.None}', "
                + $"'{HealingProviderSelection.Mock}' or '{HealingProviderSelection.Http}'");
        }
        else if (kind == HealingProviderKind.Mock && !development)
        {
            problems.Add(
                $"{HealingProviderKeys.Provider}: '{HealingProviderSelection.Mock}' is registered only in Development; "
                + $"use '{HealingProviderSelection.None}' or '{HealingProviderSelection.Http}'");
        }

        if (kind == HealingProviderKind.Http)
        {
            ValidateHttp(options, development, problems);
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>Checks the two settings the HTTP adapter cannot run without, and the shape of each.</summary>
    /// <param name="options">The bound options.</param>
    /// <param name="development">Whether the host is a Development host, where a plain-HTTP endpoint is allowed.</param>
    /// <param name="problems">The refusal lines collected so far.</param>
    private static void ValidateHttp(HealingProviderOptions options, bool development, List<string> problems)
    {
        var raw = options.BaseAddress?.Trim();

        if (string.IsNullOrEmpty(raw))
        {
            problems.Add($"{HealingProviderKeys.BaseAddress}: is required when the provider is '{HealingProviderSelection.Http}'");
        }
        else if (!Uri.TryCreate(raw, UriKind.Absolute, out var address))
        {
            problems.Add($"{HealingProviderKeys.BaseAddress}: is not an absolute URI");
        }
        else if (!address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                 && !(development && address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            // A provider key travels in an Authorization header, so plain HTTP would put it on the wire outside a
            // Development host; any other scheme is not an HTTP endpoint at all.
            problems.Add(
                $"{HealingProviderKeys.BaseAddress}: must be an https URI outside Development, and http or https in Development");
        }
        else if (address.UserInfo.Length > 0)
        {
            // Credentials in the base address would travel on every request and be readable in a log or a proxy trace;
            // the key belongs in the secret the handler resolves, so a userinfo part is refused instead of stripped.
            problems.Add($"{HealingProviderKeys.BaseAddress}: must not carry user information");
        }
        else if (!address.AbsolutePath.EndsWith('/'))
        {
            problems.Add(
                $"{HealingProviderKeys.BaseAddress}: must end with '/' so the adapter's relative path is appended to it");
        }
        else if (!string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            problems.Add($"{HealingProviderKeys.BaseAddress}: must not carry a query or a fragment");
        }

        var secret = options.ApiKeySecret?.Trim();

        if (string.IsNullOrEmpty(secret))
        {
            problems.Add($"{HealingProviderKeys.ApiKeySecret}: is required when the provider is '{HealingProviderSelection.Http}'");
        }
        else if (!string.Equals(secret, SecretNames.HealingLlmApiKey, StringComparison.Ordinal))
        {
            // One name only: any other platform secret (the database connection, a session pepper) would be presented to
            // the provider as a bearer key. The message names the key and the permitted name, never the configured value.
            problems.Add(
                $"{HealingProviderKeys.ApiKeySecret}: must name {SecretNames.HealingLlmApiKey}, the only secret this adapter presents");
        }
    }
}
