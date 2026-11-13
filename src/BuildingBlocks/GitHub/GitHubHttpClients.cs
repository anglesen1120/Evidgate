namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// The names and time budgets of the typed clients the App uses. They are separate on purpose: the token and OAuth
/// exchanges are POSTs that must never be retried, while every typed read is an idempotent GET that may be retried
/// inside one bounded budget.
/// </summary>
public static class GitHubHttpClients
{
    /// <summary>Client of the typed reads: installation, repository, trees, blobs, user and membership lookups.</summary>
    public const string Api = "Evidgate.GitHub.Api";

    /// <summary>Client of the installation-token exchange (no retry).</summary>
    public const string Token = "Evidgate.GitHub.Token";

    /// <summary>Client of the OAuth code exchange (no retry).</summary>
    public const string OAuth = "Evidgate.GitHub.OAuth";

    /// <summary>Name of the resilience pipeline wrapped around <see cref="Api"/>.</summary>
    public const string ApiPipeline = "github-api";

    /// <summary>Attempts the API pipeline may make: one call plus two retries.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Budget of one API attempt.</summary>
    public static TimeSpan AttemptTimeout(GitHubOptions options) =>
        TimeSpan.FromSeconds(options.HttpTimeoutSeconds);

    /// <summary>Budget of one whole API call: all attempts plus their delays and a little slack.</summary>
    public static TimeSpan TotalTimeout(GitHubOptions options) =>
        (AttemptTimeout(options) * MaxAttempts) + TimeSpan.FromSeconds(2);
}
