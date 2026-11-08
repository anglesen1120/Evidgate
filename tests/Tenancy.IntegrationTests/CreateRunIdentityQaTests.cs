#pragma warning disable CA1861

using System.Linq;
using System.Net;
using System.Net.Http;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA probes for 05.2.2-T1 (run identity from verified CI claims): claim-wins merge rules, unparseable-claim
/// handling, the <c>ref_name</c>/<c>pr_number</c> bounds, hostile JSON text, the NULL-safe immutability trigger,
/// and the absence of any run mutation route. Each assertion is a consumer-visible outcome, not a re-statement of
/// the implementation.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CreateRunIdentityQaTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;

    public CreateRunIdentityQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ AC3: claim wins, body never rejected

    [Fact]
    public async Task Qa_VerifiedBranchClaim_ConflictingBodyValuesAreIgnoredNotRejected()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);

        // kind/name/pr_number/head_sha are covered by the branch claim: the body must not win, and must not reject.
        // trigger_event has no verified source, so it is kept and labels the run unverified.
        var body = BodyWithRef(world,
            """{"kind":"pull_request","name":"other","pr_number":99,"head_sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","trigger_event":"push","is_fork":true}""");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-qa-branch"), body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        (await ColumnAsync(world, "pr_number")).ShouldBeNull();
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
        (await ColumnAsync(world, "base_sha")).ShouldBeNull();
        (await ColumnAsync(world, "trigger_event")).ShouldBe("push");
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
    }

    [Fact]
    public async Task Qa_VerifiedBranchClaim_BodyIsForkAloneDoesNotLabelTheRunUnverified()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);

        // is_fork is a claim-covered field (verified runs store false). A body fork value is ignored, and since no
        // body value is a field the claim does not cover, the run stays verified.
        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-qa-fork"), BodyWithRef(world, """{"is_fork":true}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("false");
    }

    // ------------------------------------------------------------------ AC3: unparseable claim

    [Fact]
    public async Task Qa_UnparseableClaim_BodyKindNameAndPrNumberIgnored_HeadAndTriggerKept()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/foo/bar", Sha);

        // refs/foo/bar covers nothing: kind/name/pr_number are null and the body is ignored for them. head_sha is not
        // covered by any claim here, so the body value is kept and the run is unverified.
        var body = BodyWithRef(world,
            """{"kind":"branch","name":"main","pr_number":12,"head_sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","trigger_event":"push"}""");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-qa-unparse"), body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBeNull();
        (await ColumnAsync(world, "ref_name")).ShouldBeNull();
        (await ColumnAsync(world, "pr_number")).ShouldBeNull();
        (await ColumnAsync(world, "head_sha")).ShouldBe(OtherSha);
        (await ColumnAsync(world, "trigger_event")).ShouldBe("push");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
    }

    // ------------------------------------------------------------------ AC4: name bound counts code points

    [Fact]
    public async Task Qa_RefName_255CodePointsWithAstralRunes_IsAccepted_AndCountedAsCodePoints()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        // 255 code points but 510 UTF-16 units: a UTF-16 length check would wrongly reject this.
        var name = string.Concat(Enumerable.Repeat("\U0001F600", 255));
        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, $$"""{"kind":"branch","name":"{{name}}"}"""));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<int>(
            world.Database, $"select char_length(ref_name) from public.run where org_id = '{world.Org}'")).ShouldBe(255);
    }

    [Fact]
    public async Task Qa_RefName_256CodePointsWithAstralRunes_IsRequestInvalid_AndWritesNothing()
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture);

        var name = string.Concat(Enumerable.Repeat("\U0001F600", 256));
        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, $$"""{"kind":"branch","name":"{{name}}"}"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ AC4: pr_number bounds and JSON types

    [Fact]
    public async Task Qa_PrNumber_Int32MaxIsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await RunCreationTests.CreateRunAsync(
            world, BodyWithRef(world, """{"kind":"pull_request","pr_number":2147483647}"""));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await ColumnAsync(world, "pr_number")).ShouldBe("2147483647");
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-1")]
    [InlineData("5.0")]
    [InlineData("\"5\"")]
    [InlineData("1e400")]
    public async Task Qa_PrNumber_OutOfRangeOrWrongJsonType_IsRequestInvalid_NotA500(string prNumber)
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture);

        var answer = await RunCreationTests.CreateRunAsync(
            world, BodyWithRef(world, $$"""{"kind":"pull_request","pr_number":{{prNumber}}}"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ AC4: hostile JSON text inside ref

    [Theory]
    [InlineData("""{"kind":"branch","name":"a\u0000b"}""")]
    [InlineData("""{"kind":"branch","name":"\ud800"}""")]
    [InlineData("""{"kind":"branch","name":"main","trigger_event":"push\u0000"}""")]
    public async Task Qa_NulOrLoneSurrogate_InsideRef_IsRequestInvalid_AndWritesNothing(string refJson)
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture);

        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, refJson));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ AC5: the trigger is NULL-safe

    [Fact]
    public async Task Qa_OwnerUpdate_SettingIdentityOnARunWithoutIdentity_FailsWithP0001()
    {
        // A NULL -> value change on a row without identity: a naive `old <> new` is NULL here and would let it pass.
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        foreach (var assignment in new[] { "ref_name = 'x'", "pr_number = 5", "is_fork = true", "unverified_ref = true" })
        {
            await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await owner.CaptureErrorAsync($"update public.run set {assignment} where id = '{runId}'");
            error.SqlState.ShouldBe("P0001", assignment);
        }

        (await ColumnAsync(world, "ref_name")).ShouldBeNull();
        (await ColumnAsync(world, "pr_number")).ShouldBeNull();
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("false");
    }

    [Fact]
    public async Task Qa_OwnerUpdate_NullToNullOnAnIdentityColumn_Succeeds()
    {
        // The other half of NULL safety: `is distinct from` must not treat NULL = NULL as a change.
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync($"update public.run set ref_name = ref_name, head_sha = head_sha where id = '{runId}'"))
            .ShouldBe(1);
        await owner.CommitAsync();
    }

    // ------------------------------------------------------------------ AC5: no mutation route

    [Theory]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Qa_RunRoute_HasNoMutationVerb_AndTheIdentityIsUnchanged(string verb)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);
        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-qa-route"), BodyWithRef(world, """{"trigger_event":"push"}"""));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var runId = await _fixture.ScalarAsync<Guid>(
            world.Database, $"select id from public.run where org_id = '{world.Org}'");

        var answer = await world.Client.SendAsync(new HttpMethod(verb), $"/v1/runs/{runId:D}", world.Admin);

        new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed }.ShouldContain(answer.Status, verb);
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
    }

    // ------------------------------------------------------------------ helpers (the same shape as CreateRunIdentityTests)

    private static string BodyWithRef(RunCreationTests.World world, string refJson) =>
        $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{{{refJson}}}}""";

    /// <summary>The repository id the <see cref="Producer"/>'s CI run reference and the seeded issue rows share.</summary>
    private const long CiRepositoryId = 987_654_321L;

    /// <summary>The run id the <see cref="Producer"/>'s CI run reference and the seeded issue rows share.</summary>
    private const long CiRunId = 42_424_242L;

    /// <summary>The run attempt the <see cref="Producer"/>'s CI run reference and the seeded issue rows share.</summary>
    private const int CiRunAttempt = 3;

    /// <summary>
    /// The 02.4.1 state a verified caller implies: the repository's <c>ci_binding</c> and the <c>ci_token_issue</c> row
    /// the exchange wrote from the presented OIDC token. The row is keyed by the run reference the minted token carries
    /// (<paramref name="repositoryId"/>/<paramref name="runId"/>/<paramref name="runAttempt"/>), never by the token's
    /// <c>jti</c>: 02.4.1 mints a fresh GUID <c>jti</c> and stores the GitHub OIDC <c>jti</c> separately, so this
    /// helper writes a <c>jti</c> deliberately unrelated to any principal's credential id (D-S45-Q2).
    /// </summary>
    private async Task SeedClaimAsync(
        string database,
        Guid org,
        Guid project,
        string gitRef,
        string sha,
        long repositoryId = CiRepositoryId,
        long runId = CiRunId,
        int runAttempt = CiRunAttempt)
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
            insert into public.ci_token_issue (id, org_id, project_id, binding_id, provider, jti, repository_id, repository_full_name, ref, sha, run_id, run_attempt, job_workflow_ref, expires_at)
            values (@id, @org_id, @project_id, @binding_id, 'github_actions', @jti, @repository_id, 'acme/web', @ref, @sha, @run_id, @run_attempt, 'acme/web/.github/workflows/ci.yml@refs/heads/main', now() + interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("binding_id", bindingId),
            ("jti", Guid.CreateVersion7().ToString("D")),
            ("repository_id", repositoryId),
            ("ref", gitRef),
            ("sha", sha),
            ("run_id", runId),
            ("run_attempt", runAttempt));
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

    private async Task<string?> ColumnAsync(RunCreationTests.World world, string column) =>
        await _fixture.ScalarAsync<string>(
            world.Database, $"select {column}::text from public.run where org_id = '{world.Org}'");
}
