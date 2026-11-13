using System.Globalization;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// Boot validation of the App client: both base addresses must stay on their pinned hosts, the App id must be the
/// numeric id GitHub issued, and the client id and slug must be safe path segments. A message names the configuration
/// key only — never a value.
/// </summary>
/// <remarks>
/// A null, empty or whitespace App id counts as ABSENT: the client reports itself unconfigured and sends nothing, which
/// lets a host start without a GitHub App. A present App id is validated, and a base address is always validated
/// because it always has the pinned default.
/// </remarks>
public sealed class GitHubOptionsValidator : IValidateOptions<GitHubOptions>
{
    private const int MaxAppIdDigits = 20;
    private const int MaxClientIdLength = 64;
    private const int MaxSlugLength = 100;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckBaseUrl(failures, options.ApiBaseUrl, nameof(GitHubOptions.ApiBaseUrl), GitHubEndpoints.ApiHost, GitHubEndpoints.IsAllowedApiBase);
        CheckBaseUrl(failures, options.OAuthBaseUrl, nameof(GitHubOptions.OAuthBaseUrl), GitHubEndpoints.OAuthHost, GitHubEndpoints.IsAllowedOAuthBase);
        CheckAppId(failures, options.AppId);
        CheckSegment(failures, options.ClientId, nameof(GitHubOptions.ClientId), MaxClientIdLength);
        CheckSegment(failures, options.AppSlug, nameof(GitHubOptions.AppSlug), MaxSlugLength);

        if (options.InstallationTokenSafetyMarginSeconds * 2 >= 3600)
        {
            failures.Add($"{GitHubOptions.SectionName}:{nameof(GitHubOptions.InstallationTokenSafetyMarginSeconds)} must stay below half of the one-hour installation-token lifetime");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckBaseUrl(
        List<string> failures,
        string? value,
        string member,
        string host,
        Func<Uri, bool> allowed)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !allowed(uri))
        {
            failures.Add($"{GitHubOptions.SectionName}:{member} must be https://{host} with no userinfo, path, query or non-default port");
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

    private static void CheckSegment(List<string> failures, string? value, string member, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Absent: the install flow reports itself unconfigured instead of building a URL with an empty segment.
            return;
        }

        if (value.Length > maxLength || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            failures.Add($"{GitHubOptions.SectionName}:{member} must be at most {maxLength} characters of ASCII letters, digits, '-' or '_'");
        }
    }
}
