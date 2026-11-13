using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 03.4.1 through the real route: a member registers a repository for their project, and the Api verifies it through the
/// org's GitHub installation on the way in. Every test drives <c>POST /v1/projects/{p}/git-repo-bindings</c> against a
/// booted host on a migrated database, with the GitHub pipe replaced by <see cref="CatalogGitHubHttpHandler"/>, so the
/// statuses, the rows and the audit trail are the host's own, never a service called directly.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CatalogGitHubBindingApiTests
{
    private readonly PostgresFixture _postgres;

    public CatalogGitHubBindingApiTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 03.4.1-T1.4: the verified repository is stored with the branch GitHub reported, its audit row is written with it,
    /// and the webhook secret reference is the one derived from the new binding id (11.3.1's reference model, never a
    /// value the request could name).
    /// </summary>
    [Fact]
    public async Task Registration_Answers201_AndStoresTheServerDerivedSecretReference()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(_postgres, cancellationToken: Token);

        using var response = await site.RegisterAsync("binding-happy", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await ReadJsonAsync(response);
        var id = body.RootElement.GetProperty("id").GetGuid();
        body.RootElement.GetProperty("project_id").GetGuid().ShouldBe(site.Project);
        body.RootElement.GetProperty("installation_id").GetInt64().ShouldBe(site.InstallationId);
        body.RootElement.GetProperty("repository_id").GetInt64().ShouldBe(CatalogGitHubBindingSite.RepositoryNumber);
        body.RootElement.GetProperty("default_branch").GetString().ShouldBe("main");
        body.RootElement.GetProperty("sync_state").GetString().ShouldBe("active");
        (await site.BindingCountAsync()).ShouldBe(1);
        (await site.AuditCountAsync()).ShouldBe(1);
        (await site.PersistedSecretReferenceAsync()).ShouldBe($"secret:gh_wh_{id:N}");
    }

    /// <summary>
    /// 00 §9.1 through this route: the same key replays the stored answer verbatim and reaches no further GitHub call,
    /// even when the upstream renamed the branch and went down since — the retry must never depend on the verification
    /// it replaces.
    /// </summary>
    [Fact]
    public async Task Replay_AnswersTheSameResponse_WithoutAnyFurtherGitHubCall()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(_postgres, cancellationToken: Token);
        using var first = await site.RegisterAsync("binding-replay", Token);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var firstBody = await ReadJsonAsync(first);
        var id = firstBody.RootElement.GetProperty("id").GetGuid();
        var callsAfterFirst = site.GitHub.Requests.Length;

        site.GitHub.DefaultBranch = "trunk";
        site.GitHub.Outage = true;
        using var replay = await site.RegisterAsync("binding-replay", Token);

        replay.StatusCode.ShouldBe(first.StatusCode);
        using var replayBody = await ReadJsonAsync(replay);
        replayBody.RootElement.GetProperty("id").GetGuid().ShouldBe(id);
        replayBody.RootElement.GetProperty("default_branch").GetString().ShouldBe("main");
        site.GitHub.Requests.Length.ShouldBe(callsAfterFirst);
        (await site.BindingCountAsync()).ShouldBe(1);
        (await site.AuditCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// The project key of §4: a second key for a repository the same project already binds is 409
    /// <c>binding_exists</c>, and the stored binding is not duplicated.
    /// </summary>
    [Fact]
    public async Task SameProject_RegisteringTheBoundRepositoryAgain_Is409()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(_postgres, cancellationToken: Token);
        using var first = await site.RegisterAsync("binding-first", Token);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var second = await site.RegisterAsync("binding-second", Token);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(second)).ShouldBe("binding_exists");
        (await site.BindingCountAsync()).ShouldBe(1);
        (await site.AuditCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// The global key of §4: a repository another org already bound in the same installation is 409
    /// <c>binding_conflict</c>; the calling org gets no row and no audit row, and the other org's binding is untouched.
    /// </summary>
    [Fact]
    public async Task RepositoryBoundByAnotherOrg_Is409_AndTheBindingStaysTheirs()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(
            _postgres,
            new CatalogGitHubBindingSetup { RepositoryBoundByAnotherOrg = true },
            Token);

        using var response = await site.RegisterAsync("binding-cross-org", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("binding_conflict");
        (await site.BindingCountAsync()).ShouldBe(0);
        (await site.AuditCountAsync()).ShouldBe(0);
        (await site.OtherOrgBindingCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// T1.4's done-when: a repository the installation cannot read is the caller's 422 <c>repository_unreachable</c> —
    /// nothing is stored and nothing is audited, because the verification never accepted the repository.
    /// </summary>
    [Fact]
    public async Task InstallationCannotReadTheRepository_Is422_AndWritesNothing()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(_postgres, cancellationToken: Token);
        site.GitHub.RepositoryUnreachable = true;

        using var response = await site.RegisterAsync("binding-unreachable", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("repository_unreachable");
        (await site.BindingCountAsync()).ShouldBe(0);
        (await site.AuditCountAsync()).ShouldBe(0);
        site.GitHub.Requests.ShouldNotBeEmpty();
    }

    /// <summary>
    /// §8's authority rule: an installation another org installed is refused 403 <c>installation_not_owned</c> by the
    /// ownership unit, before any outbound call — the handler sees no request, and no binding or audit row is written.
    /// </summary>
    [Fact]
    public async Task ForeignOrgInstallation_Is403_WithoutCallingGitHubOrWriting()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(
            _postgres,
            new CatalogGitHubBindingSetup { InstallationOwnedByAnotherOrg = true },
            Token);

        using var response = await site.RegisterAsync("binding-foreign", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("installation_not_owned");
        site.GitHub.Requests.ShouldBeEmpty();
        (await site.BindingCountAsync()).ShouldBe(0);
        (await site.AuditCountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// §8's secret rule (S3): the body admits no webhook secret reference — a caller that names one is refused by that
    /// name (422 <c>request_invalid</c>) before the flow runs, so no outbound call happens and nothing is written.
    /// </summary>
    [Fact]
    public async Task CallerSuppliedWebhookSecretReference_IsRefusedByName()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(_postgres, cancellationToken: Token);

        using var response = await site.RegisterAsync(
            "binding-secret-ref",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["installation_id"] = site.InstallationId,
                ["repository_id"] = CatalogGitHubBindingSite.RepositoryNumber,
                ["webhook_secret_ref"] = "secret:caller-chosen",
            },
            Token);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using var body = await ReadJsonAsync(response);
        var error = body.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("request_invalid");
        var detail = error.GetProperty("details").EnumerateArray().ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("webhook_secret_ref");
        detail.GetProperty("reason").GetString().ShouldBe("unknown_field");
        site.GitHub.Requests.ShouldBeEmpty();
        (await site.BindingCountAsync()).ShouldBe(0);
        (await site.AuditCountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A host with no GitHub App configured cannot verify a repository: 503 <c>github_not_configured</c>, no outbound
    /// call, and no binding or audit row.
    /// </summary>
    [Fact]
    public async Task NoGitHubAppConfigured_Is503_AndWritesNothing()
    {
        await using var site = await CatalogGitHubBindingSite.StartAsync(
            _postgres,
            new CatalogGitHubBindingSetup { GitHubConfigured = false },
            Token);

        using var response = await site.RegisterAsync("binding-unconfigured", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ErrorCodeAsync(response)).ShouldBe(GitHubErrorCodes.NotConfigured);
        site.GitHub.Requests.ShouldBeEmpty();
        (await site.BindingCountAsync()).ShouldBe(0);
        (await site.AuditCountAsync()).ShouldBe(0);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token),
            cancellationToken: Token);

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);

        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
