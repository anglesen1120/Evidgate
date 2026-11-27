using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Authoring.Trackers.Application;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// What the provider client reads of one tracker's OAuth settings. Each tracker has its own flat options class so its
/// keys are one <c>EVIDGATE__TRACKERS__&lt;PROVIDER&gt;__*</c> group, like the other provider clients.
/// </summary>
internal interface ITrackerClientSettings
{
    /// <summary>OAuth client id.</summary>
    string? ClientId { get; }

    /// <summary>OAuth client secret.</summary>
    string? ClientSecret { get; }

    /// <summary>The fixed callback this deployment registered with the provider.</summary>
    string? RedirectUri { get; }

    /// <summary>True when all three values are present, which is what makes the provider available.</summary>
    bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}

/// <summary>
/// The Jira Cloud (Atlassian 3LO) client. The client secret has an internal setter, so the binder skips it and
/// <see cref="TrackerOAuthOptionsSecretPostConfigure"/> is its only writer; it is never logged or put in a message.
/// A deployment without it still boots: Jira Cloud answers 409 <c>tracker_provider_unavailable</c>.
/// </summary>
internal sealed class JiraCloudTrackerOptions : ITrackerClientSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:TRACKERS:JIRACLOUD";

    /// <summary>OAuth client id; plain configuration.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth client secret, resolved through <c>ISecretProvider</c>.</summary>
    public string? ClientSecret { get; internal set; }

    /// <summary>The fixed callback this deployment registered with the provider; the browser is never sent anywhere else.</summary>
    public string? RedirectUri { get; init; }
}

/// <summary>The Linear client; see <see cref="JiraCloudTrackerOptions"/> for the shared rules.</summary>
internal sealed class LinearTrackerOptions : ITrackerClientSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:TRACKERS:LINEAR";

    /// <summary>OAuth client id; plain configuration.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth client secret, resolved through <c>ISecretProvider</c>.</summary>
    public string? ClientSecret { get; internal set; }

    /// <summary>The fixed callback this deployment registered with the provider; the browser is never sent anywhere else.</summary>
    public string? RedirectUri { get; init; }
}

/// <summary>Resolves the two client secrets through <see cref="ISecretProvider"/> after the binder has run.</summary>
internal sealed class TrackerOAuthOptionsSecretPostConfigure(ISecretProvider secrets)
    : IPostConfigureOptions<JiraCloudTrackerOptions>, IPostConfigureOptions<LinearTrackerOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, JiraCloudTrackerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ClientSecret = secrets.GetSecret(SecretNames.TrackersJiraCloudClientSecret);
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, LinearTrackerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ClientSecret = secrets.GetSecret(SecretNames.TrackersLinearClientSecret);
    }
}

/// <summary>
/// Boot validation: a present value must be well formed, and a present redirect URI must be this deployment's own
/// https callback for that provider. A blank value is absent, not an error. A message names the key only, never a value.
/// </summary>
internal sealed class TrackerOAuthOptionsValidator : IValidateOptions<JiraCloudTrackerOptions>, IValidateOptions<LinearTrackerOptions>
{
    private const int MaxClientIdLength = 256;
    private const int MaxClientSecretLength = 1024;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, JiraCloudTrackerOptions options) =>
        Check(JiraCloudTrackerOptions.SectionName, options, TrackerProvider.JiraCloud);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LinearTrackerOptions options) =>
        Check(LinearTrackerOptions.SectionName, options, TrackerProvider.Linear);

    private static ValidateOptionsResult Check(string prefix, ITrackerClientSettings? settings, TrackerProvider kind)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();
        Credential(failures, $"{prefix}:{nameof(ITrackerClientSettings.ClientId)}", settings.ClientId, MaxClientIdLength);
        Credential(failures, $"{prefix}:{nameof(ITrackerClientSettings.ClientSecret)}", settings.ClientSecret, MaxClientSecretLength);

        if (!string.IsNullOrWhiteSpace(settings.RedirectUri)
            && (!Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || uri.UserInfo.Length != 0
                || uri.Query.Length != 0
                || uri.Fragment.Length != 0
                || !string.Equals(uri.AbsolutePath, TrackerEndpoints.CallbackPath(kind), StringComparison.Ordinal)))
        {
            failures.Add($"{prefix}:{nameof(ITrackerClientSettings.RedirectUri)} must be an https address ending in {TrackerEndpoints.CallbackPath(kind)} with no userinfo, query or fragment");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Credential(List<string> failures, string key, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > maxLength || value.AsSpan().ContainsAny(" \t\r\n"))
        {
            failures.Add($"{key} must be a single non-empty value of at most {maxLength} characters");
        }
    }
}
