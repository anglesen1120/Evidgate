using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login.GitHub;

/// <summary>
/// Resolves the GitHub client secret through <see cref="ISecretProvider"/> after the binder has run
/// (10.1.1-T2), following <c>CiAuthOptionsSecretPostConfigure</c>. The property has an internal setter,
/// so <c>BindConfiguration</c> skips it and this is its only writer; validation runs after
/// post-configuration, so <see cref="GitHubLoginOptionsValidator"/> still sees the resolved value and
/// still names only the key.
/// </summary>
internal sealed class GitHubLoginOptionsSecretPostConfigure : IPostConfigureOptions<GitHubLoginOptions>
{
    private readonly ISecretProvider _secrets;

    public GitHubLoginOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, GitHubLoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ClientSecret = _secrets.GetSecret(SecretNames.OauthGithubClientSecret);
    }
}

/// <summary>
/// Boot validation of the GitHub login options: every endpoint that is present must still be an https
/// address of the fixed S4 allowlist (an override pointing anywhere else is a configuration attack, not a
/// preference), and present credentials must be single non-empty values.
/// </summary>
/// <remarks>
/// <para>
/// Null, empty and whitespace are one case here: ABSENT. A deployment that injects an empty value for an
/// optional key leaves the provider unconfigured (the routes answer 503) instead of failing the host at
/// boot, exactly like a key that was never set.
/// </para>
/// <para>
/// A message names the configuration key only. It never repeats a value, because the client secret and
/// the client id travel through the same object.
/// </para>
/// </remarks>
internal sealed class GitHubLoginOptionsValidator : IValidateOptions<GitHubLoginOptions>
{
    private const int MaxClientIdLength = 256;
    private const int MaxClientSecretLength = 1024;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GitHubLoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckUri(failures, nameof(GitHubLoginOptions.TokenUrl), options.TokenUrl, GitHubEndpoints.IsAllowlistedOutbound,
            "must be https on an allowlisted GitHub host with no userinfo and the default port (S4)");
        CheckUri(failures, nameof(GitHubLoginOptions.ApiBaseUrl), options.ApiBaseUrl, GitHubEndpoints.IsAllowlistedOutbound,
            "must be https on an allowlisted GitHub host with no userinfo and the default port (S4)");
        CheckUri(failures, nameof(GitHubLoginOptions.AuthorizeUrl), options.AuthorizeUrl, GitHubEndpoints.IsAllowlistedAuthorization,
            "must be an https address of github.com with no userinfo and the default port");

        CheckCredential(failures, nameof(GitHubLoginOptions.ClientId), options.ClientId, MaxClientIdLength);
        CheckCredential(failures, nameof(GitHubLoginOptions.ClientSecret), options.ClientSecret, MaxClientSecretLength);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckUri(List<string> failures, string property, string? value, Func<Uri, bool> allowlisted, string rule)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Absent: the provider reports itself unconfigured and the routes answer 503.
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !allowlisted(uri))
        {
            failures.Add($"{GitHubLoginOptions.SectionName}:{property} {rule}");
        }
    }

    private static void CheckCredential(List<string> failures, string property, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Absent credentials leave the provider unconfigured, which the routes answer with 503.
            return;
        }

        if (value.Length > maxLength || value.AsSpan().ContainsAny(" \t\r\n"))
        {
            failures.Add($"{GitHubLoginOptions.SectionName}:{property} must be a single non-empty value of at most {maxLength} characters");
        }
    }
}
