using System.Globalization;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// Boot validation of the App client: the base address must stay on the pinned GitHub API host
/// (S4), the App id must be the numeric id GitHub issued, and the private-key reference must be a
/// registered secret name. A message names the configuration key only — never a value.
/// </summary>
/// <remarks>
/// A null, empty or whitespace App id counts as ABSENT: the client reports itself unconfigured and
/// sends nothing, which lets a host start without a GitHub App. A present App id is validated, and
/// a base address is always validated because it always has the pinned default.
/// </remarks>
internal sealed class GitHubOptionsValidator : IValidateOptions<GitHubOptions>
{
    private const int MaxAppIdDigits = 20;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckBaseUrl(failures, options.ApiBaseUrl);
        CheckAppId(failures, options.AppId);

        if (options.InstallationTokenSafetyMarginSeconds * 2 >= 3600)
        {
            failures.Add($"{GitHubOptions.SectionName}:{nameof(GitHubOptions.InstallationTokenSafetyMarginSeconds)} must stay below half of the one-hour installation-token lifetime");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckBaseUrl(List<string> failures, string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !GitHubEndpoints.IsAllowedApiBase(uri))
        {
            failures.Add($"{GitHubOptions.SectionName}:{nameof(GitHubOptions.ApiBaseUrl)} must be https://{GitHubEndpoints.ApiHost} with no userinfo, path, query or non-default port (S4)");
        }
    }

    private static void CheckAppId(List<string> failures, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Absent: the client is unconfigured and every call returns github_not_configured.
            return;
        }

        var digits = value.AsSpan();
        if (digits.Length > MaxAppIdDigits
            || digits.ContainsAnyExceptInRange('0', '9')
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            failures.Add($"{GitHubOptions.SectionName}:{nameof(GitHubOptions.AppId)} must be the numeric App id GitHub issued, of at most {MaxAppIdDigits} digits");
        }
    }

}
