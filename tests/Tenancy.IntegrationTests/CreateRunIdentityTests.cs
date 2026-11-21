using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1 AC1–AC4/AC9 through the real host and a real PostgreSQL: the identity a run stores and reports, the
/// verified <c>ci_token_issue</c> claim that wins over the body, the <c>unverified_ref</c> label, and the idempotency
/// key that now covers the caller's CI run reference.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CreateRunIdentityTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public CreateRunIdentityTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private static readonly string Sha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);

    /// <summary>The repository id the <see cref="Producer"/>'s CI run reference and the seeded issue rows share.</summary>
    private const long CiRepositoryId = 987_654_321L;

    /// <summary>The run id the <see cref="Producer"/>'s CI run reference and the seeded issue rows share.</summary>
    private const long CiRunId = 42_424_242L;

    /// <summary>The run attempt the <see cref="Producer"/>'s CI run reference and the seeded issue rows share.</summary>
    private const int CiRunAttempt = 3;

    /// <summary>A create body whose <c>ref</c> member is <paramref name="refJson"/> verbatim (an object, or a bare value).</summary>
    private static string BodyWithRef(RunCreationTests.World world, string refJson) =>
        $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{{{refJson}}}}""";

    /// <summary>
    /// The 02.4.1 state a verified caller implies: the repository's <c>ci_binding</c> and the <c>ci_token_issue</c> row
    /// the exchange wrote from the presented OIDC token. The row is keyed by the run reference the minted token carries
    /// (<paramref name="repositoryId"/>/<paramref name="runId"/>/<paramref name="runAttempt"/>), never by the token's
    /// <c>jti</c>: 02.4.1 mints a fresh GUID <c>jti</c> and stores the GitHub OIDC <c>jti</c> separately, so this
    /// helper writes a <c>jti</c> deliberately unrelated to any principal's credential id (D-S45-Q2). A second call for
    /// the same project reuses the binding and appends another issue row, so an attempt that exchanged several tokens
    /// (or an older row carrying another ref) is expressible; <paramref name="createdAt"/> pins <c>created_at</c> when a
    /// test needs a defined newest row (the column defaults to <c>now()</c>, the transaction clock).
    /// </summary>
    private async Task SeedClaimAsync(
        string database,
        Guid org,
        Guid project,
        string gitRef,
        string sha,
        long repositoryId = CiRepositoryId,
        long runId = CiRunId,
        int runAttempt = CiRunAttempt,
        DateTimeOffset? createdAt = null,
        string? jti = null)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org, project);
        await scope.ExecuteAsync(
            """
            insert into public.ci_binding (id, org_id, project_id, provider, repository_id, repository_full_name, allowed_ref_patterns, allowed_workflow_refs)
            values (@id, @org_id, @project_id, 'github_actions', @repository_id, 'acme/web', array['refs/heads/*', 'refs/tags/*', 'refs/pull/*/merge'], array['acme/web/.github/workflows/ci.yml@refs/heads/main'])
            on conflict (org_id, project_id, provider, repository_id) do nothing
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("repository_id", repositoryId));
        var bindingId = await scope.ScalarAsync<Guid>(
            """
            select id from public.ci_binding
             where org_id = @org_id and project_id = @project_id and provider = 'github_actions' and repository_id = @repository_id
            """,
            ("org_id", org),
            ("project_id", project),
            ("repository_id", repositoryId));
        await scope.ExecuteAsync(
            """
            insert into public.ci_token_issue (id, org_id, project_id, binding_id, provider, jti, repository_id, repository_full_name, ref, sha, run_id, run_attempt, job_workflow_ref, expires_at, created_at)
            values (@id, @org_id, @project_id, @binding_id, 'github_actions', @jti, @repository_id, 'acme/web', @ref, @sha, @run_id, @run_attempt, 'acme/web/.github/workflows/ci.yml@refs/heads/main', now() + interval '1 hour', coalesce(@created_at::timestamptz, now()))
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("binding_id", bindingId),
            ("jti", jti ?? Guid.CreateVersion7().ToString("D")),
            ("repository_id", repositoryId),
            ("ref", gitRef),
            ("sha", sha),
            ("run_id", runId),
            ("run_attempt", runAttempt),
            ("created_at", (object?)createdAt ?? DBNull.Value));
        await scope.CommitAsync();
    }

    /// <summary>
    /// A <c>ci_producer</c> principal: a minted token whose <c>jti</c> is <paramref name="tokenId"/> (unrelated to the
    /// issue row's <c>jti</c>) and which carries the CI run reference the issue lookup keys on.
    /// </summary>
    private static RequestPrincipal Producer(Guid project, string tokenId, long runId = CiRunId, int attempt = CiRunAttempt) =>
        RequestPrincipal.Service(
            project,
            ["run:create"],
            credentialId: tokenId,
            ciRun: new CiRunReference("github_actions", CiRepositoryId, runId, attempt));

    /// <summary>One identity column of the world's run, as text (null when the column is null).</summary>
    private async Task<string?> ColumnAsync(RunCreationTests.World world, string column) =>
        await _fixture.ScalarAsync<string>(
            world.Database, $"select {column}::text from public.run where org_id = '{world.Org:D}'");

    /// <summary>One expression of the world's own outbox rows; the shared database also holds other tests' rows.</summary>
    private Task<T> OutboxAsync<T>(RunCreationTests.World world, string expression) =>
        _fixture.ScalarAsync<T>(world.Database, $"select {expression} from public.outbox where org_id = '{world.Org:D}'");

    private static async Task<ApiAnswer> GetRunAsync(RunCreationTests.World world, Guid runId) =>
        await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId:D}", world.Admin);

    // ------------------------------------------------------------------ AC1

    [Fact]
    public async Task CreateRun_OidcClaims_StoresIdentity()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-branch"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeFalse();
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        (await ColumnAsync(world, "pr_number")).ShouldBeNull();
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
        (await ColumnAsync(world, "base_sha")).ShouldBeNull();
        (await ColumnAsync(world, "trigger_event")).ShouldBeNull();
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("false");
    }

    [Fact]
    public async Task CreateRun_VerifiedTagClaim_StoresTheTagAndItsSha()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/tags/v1.0.0", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-tag"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("tag");
        (await ColumnAsync(world, "ref_name")).ShouldBe("v1.0.0");
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
        result.Value.UnverifiedRef.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRun_VerifiedPullRequestClaim_StoresTheNumberButNotTheTestMergeSha()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/pull/12/merge", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-pr"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("pull_request");
        (await ColumnAsync(world, "ref_name")).ShouldBe("12/merge");
        (await ColumnAsync(world, "pr_number")).ShouldBe("12");
        // The sha claim of a pull-request ref is GitHub's test-merge commit, so it is never the head of the run.
        (await ColumnAsync(world, "head_sha")).ShouldBeNull();
        result.Value.UnverifiedRef.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRun_VerifiedCaller_OutboxCarriesTheIdentityBlock()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-outbox"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        // The three pre-existing members are unchanged, so a consumer that reads only them keeps working.
        (await OutboxAsync<string>(world, "payload ->> 'run_id'")).ShouldBe(result.Value.RunId.ToString("D"));
        (await OutboxAsync<string>(world, "payload ->> 'project_id'")).ShouldBe(world.Project.ToString("D"));
        (await OutboxAsync<string>(world, "payload ->> 'manifest_id'")).ShouldBe(world.ManifestId.ToString("D"));

        // The additive block: all seven members, the claim's values, and the label beside it.
        (await OutboxAsync<long>(world, "(select count(*) from jsonb_object_keys(payload -> 'ref'))")).ShouldBe(7);
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'kind'")).ShouldBe("branch");
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'name'")).ShouldBe("main");
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'head_sha'")).ShouldBe(Sha);
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'trigger_event'")).ShouldBeNull();
        (await OutboxAsync<string>(world, "payload -> 'unverified_ref'")).ShouldBe("false");
    }

    [Fact]
    public async Task CreateRun_VerifiedCaller_GetRunReportsTheIdentityBlock()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-read"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var answer = await GetRunAsync(world, result.Value.RunId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeFalse();
        var identity = answer.Body.GetProperty("ref");

        // Every member of the block is present, nulls included (05.2.2 §5), so the wire shape is stable for a client.
        identity.EnumerateObject().Select(member => member.Name)
            .ShouldBe(["kind", "name", "pr_number", "head_sha", "base_sha", "trigger_event", "is_fork"]);
        identity.GetProperty("kind").GetString().ShouldBe("branch");
        identity.GetProperty("name").GetString().ShouldBe("main");
        identity.GetProperty("head_sha").GetString().ShouldBe(Sha);
        identity.GetProperty("pr_number").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("base_sha").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("trigger_event").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("is_fork").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRun_GetRun_RunWithoutIdentity_AllMembersNull()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var answer = await GetRunAsync(world, created.Id("run_id"));

        var identity = answer.Body.GetProperty("ref");
        identity.GetProperty("kind").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("name").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("is_fork").GetBoolean().ShouldBeFalse();
        answer.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeFalse("a run with no identity has nothing unverified");
    }

    // ------------------------------------------------------------------ AC2: body-only identity

    [Fact]
    public async Task CreateRun_NoClaims_UnverifiedRef()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "token-inbound");
        var body = BodyWithRef(
            world,
            $$$"""{"kind":"branch","name":"feature/05.2.2-T1","head_sha":"{{{Sha}}}","base_sha":"{{{OtherSha}}}","trigger_event":"dispatch","is_fork":false}""");

        var result = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, principal, body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeTrue("every stored value came from the body");
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("feature/05.2.2-T1");
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
        (await ColumnAsync(world, "base_sha")).ShouldBe(OtherSha);
        (await ColumnAsync(world, "trigger_event")).ShouldBe("dispatch");
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
    }

    [Fact]
    public async Task CreateRun_NoClaimNoRef_StoresNoIdentityAndIsNotUnverified()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "token-empty");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, principal, RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeFalse();
        (await ColumnAsync(world, "ref_kind")).ShouldBeNull();
        (await ColumnAsync(world, "ref_name")).ShouldBeNull();
        (await ColumnAsync(world, "head_sha")).ShouldBeNull();
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("false");
    }

    [Fact]
    public async Task CreateRun_ForkBodyValue_IsStoredAndUnverified()
    {
        // Only the body can set is_fork: a fork PR exchange is refused 403 by 02.4.1, so no verified caller is a fork.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "token-fork");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api,
            world.Org,
            world.Project,
            principal,
            BodyWithRef(world, """{"kind":"pull_request","name":"7/merge","pr_number":7,"is_fork":true}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "is_fork")).ShouldBe("true");
        (await ColumnAsync(world, "pr_number")).ShouldBe("7");
        result.Value.UnverifiedRef.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ AC3: the claim wins

    [Fact]
    public async Task CreateRun_ClaimConflict_ClaimWins()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);
        var body = BodyWithRef(
            world,
            $$$"""{"kind":"pull_request","name":"999/merge","pr_number":999,"head_sha":"{{{OtherSha}}}","trigger_event":"push","is_fork":true}""");

        var result = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, Producer(world.Project, "token-conflict"), body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        // The body is ignored, never rejected, for every member the claim covers (AC3).
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        (await ColumnAsync(world, "pr_number")).ShouldBeNull();
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");

        // trigger_event has no verified source, so the body's value is what is stored — and it labels the run.
        (await ColumnAsync(world, "trigger_event")).ShouldBe("push");
        result.Value.UnverifiedRef.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateRun_VerifiedCallerWithoutIssueRow_IsTreatedAsUnverified()
    {
        // A token that names a CI run but has no ci_token_issue row (purged, or never written) is not verified: the
        // body is stored and labelled rather than trusted (fail toward advisory).
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api,
            world.Org,
            world.Project,
            Producer(world.Project, "token-absent"),
            BodyWithRef(world, """{"kind":"tag","name":"v9"}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("tag");
        (await ColumnAsync(world, "ref_name")).ShouldBe("v9");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
        result.Value.UnverifiedRef.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateRun_AnotherProjectsIssueRow_DoesNotVerifyTheCaller()
    {
        // The claim is read for this project (and this run attempt), so a row minted for another project of the same org
        // can never verify — even though its run reference matches this caller's.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        await SeedClaimAsync(world.Database, world.Org, other, "refs/heads/main", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api,
            world.Org,
            world.Project,
            Producer(world.Project, "token-foreign"),
            BodyWithRef(world, """{"kind":"branch","name":"feature/x"}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("feature/x");
        result.Value.UnverifiedRef.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateRun_SameAttemptSeveralIssueRows_NewestRowWinsWithoutException()
    {
        // One run attempt can exchange several tokens, so it can have several ci_token_issue rows. ref/sha are fixed
        // per attempt, so the newest row is authoritative: an older row carrying another ref must not win, and two
        // matching rows must not make the read throw (the statement takes limit 1).
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(
            world.Database, world.Org, world.Project, "refs/heads/stale", Sha, createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", OtherSha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-newest"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        (await ColumnAsync(world, "head_sha")).ShouldBe(OtherSha);
        result.Value.UnverifiedRef.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRun_DifferentRunAttempt_DoesNotVerifyTheCaller()
    {
        // The run attempt is part of the lookup key: a row of the same repository and run but another attempt is not
        // this caller's, so the caller is unverified and its body is stored and labelled.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api,
            world.Org,
            world.Project,
            Producer(world.Project, "token-other-attempt", attempt: CiRunAttempt + 1),
            BodyWithRef(world, """{"kind":"branch","name":"feature/x"}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("feature/x");
        result.Value.UnverifiedRef.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateRun_MintedTokenJtiDiffersFromIssueRowJti_StillVerifies()
    {
        // The 02.4.1 exchange mints a fresh GUID jti for the presented token and stores the GitHub OIDC jti in the
        // row, so the principal's credential id and the row's jti are unrelated (the production shape): the run
        // reference, not the jti, is what links them.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await SeedClaimAsync(
            world.Database, world.Org, world.Project, "refs/heads/main", Sha, jti: "github-oidc-jwt-jti-42");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "minted-token-jti-9f2c1a"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        result.Value.UnverifiedRef.ShouldBeFalse();
    }
}
