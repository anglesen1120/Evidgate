// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1.3/AC1–AC4 as pure functions: the body <c>ref</c> shape rules, the verified <c>ref</c>/<c>sha</c> claim
/// mapping, the claim-wins merge, the <c>unverified_ref</c> matrix and the effective-value rule — all without a
/// database, so a trust-policy change fails here before it can reach the served route.
/// </summary>
public sealed class RunIdentityRulesTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static RunRefInput Input(
        string? kind = null,
        string? name = null,
        int? prNumber = null,
        string? headSha = null,
        string? baseSha = null,
        string? triggerEvent = null,
        bool isFork = false) =>
        new(kind, name, prNumber, headSha, baseSha, triggerEvent, isFork);

    private static readonly string Sha1 = new('a', 40);
    private static readonly string Sha2 = new('b', 40);
    private static readonly string Sha256 = new('c', 64);

    // ------------------------------------------------------------------ claim mapping (AC1)

    [Fact]
    public void MapClaim_RecognisesTheThreeGitHubRefShapes()
    {
        RunIdentityRules.MapClaim("refs/heads/main").ShouldBe(("branch", "main", (int?)null));
        RunIdentityRules.MapClaim("refs/heads/feature/05.2.2-T1").ShouldBe(("branch", "feature/05.2.2-T1", (int?)null));
        RunIdentityRules.MapClaim("refs/tags/v1.0.0").ShouldBe(("tag", "v1.0.0", (int?)null));
        RunIdentityRules.MapClaim("refs/pull/12/merge").ShouldBe(("pull_request", "12/merge", (int?)12));
        RunIdentityRules.MapClaim("refs/pull/1/merge").ShouldBe(("pull_request", "1/merge", (int?)1));
    }

    [Fact]
    public void MapClaim_PrNumberWithLeadingZeros_StoresTheCanonicalDecimalAndNumber()
    {
        // GitHub never writes a leading zero, but the digits slice may carry one: the stored name and number are the
        // canonical decimal (F3), so `refs/pull/007/merge` is `7/merge` with number 7, not the raw `007/merge`.
        RunIdentityRules.MapClaim("refs/pull/007/merge").ShouldBe(("pull_request", "7/merge", (int?)7));
        RunIdentityRules.MapClaim("refs/pull/00012/merge").ShouldBe(("pull_request", "12/merge", (int?)12));
    }

    [Theory]
    [InlineData("refs/heads/a\nb")]
    [InlineData("refs/heads/a\rb")]
    [InlineData("refs/heads/a\tb")]
    [InlineData("refs/heads/a\u007Fb")]
    [InlineData("refs/heads/a\u0085b")]
    [InlineData("refs/tags/a\nb")]
    public void MapClaim_RefNameWithAControlCharacter_CoversNothing(string gitRef)
    {
        // A git ref name cannot carry a control character, so a claim that does covers nothing (SEC-F3).
        RunIdentityRules.MapClaim(gitRef).ShouldBe(((string?)null, (string?)null, (int?)null));
    }

    [Fact]
    public void MapClaim_UnparseableRef_CoversNothing()
    {
        // Nothing about an unrecognised shape is trusted, so the three members stay null and the body is ignored for
        // them rather than reinterpreted (05.2.2 §4).
        RunIdentityRules.MapClaim("refs/notes/x").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/heads/").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/tags/").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/pull/12/head").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/pull//merge").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/pull/12/merge/").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("main").ShouldBe(((string?)null, (string?)null, (int?)null));

        // A number the column cannot hold (integer, > 0) is dropped with the rest of the shape.
        RunIdentityRules.MapClaim("refs/pull/99999999999/merge").ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/pull/0/merge").ShouldBe(((string?)null, (string?)null, (int?)null));

        // A name over 255 code points cannot be stored, so the claim covers nothing rather than aborting the insert.
        RunIdentityRules.MapClaim("refs/heads/" + new string('x', 256)).ShouldBe(((string?)null, (string?)null, (int?)null));
        RunIdentityRules.MapClaim("refs/heads/" + new string('x', 255)).ShouldBe(("branch", new string('x', 255), (int?)null));
    }

    [Fact]
    public void ClaimShaOrNull_OnlyTheColumnShapes()
    {
        RunIdentityRules.ClaimShaOrNull(Sha1).ShouldBe(Sha1);
        RunIdentityRules.ClaimShaOrNull(Sha256).ShouldBe(Sha256);

        // The exchange constrains neither, so anything else is dropped instead of surfacing as a 23514.
        RunIdentityRules.ClaimShaOrNull(new string('a', 41)).ShouldBeNull();
        RunIdentityRules.ClaimShaOrNull(new string('a', 39)).ShouldBeNull();
        RunIdentityRules.ClaimShaOrNull(new string('A', 40)).ShouldBeNull();
        RunIdentityRules.ClaimShaOrNull(new string('g', 40)).ShouldBeNull();
        RunIdentityRules.ClaimShaOrNull(string.Empty).ShouldBeNull();
    }

    // ------------------------------------------------------------------ claim-wins merge (AC1–AC3)

    [Fact]
    public void Resolve_BranchClaim_StoresTheClaimAndTheClaimSha()
    {
        RunIdentityRules.Resolve(null, new CiRefClaim("refs/heads/main", Sha1))
            .ShouldBe(new RunIdentity("branch", "main", null, Sha1, null, null, false, false));
    }

    [Fact]
    public void Resolve_TagClaim_StoresTheClaimAndTheClaimSha()
    {
        RunIdentityRules.Resolve(null, new CiRefClaim("refs/tags/v1.0.0", Sha256))
            .ShouldBe(new RunIdentity("tag", "v1.0.0", null, Sha256, null, null, false, false));
    }

    [Fact]
    public void Resolve_PullRequestClaim_DoesNotStoreTheShaClaim()
    {
        // GitHub's sha claim for a pull-request ref is the test-merge commit, not the head, so storing it would
        // misreport the commit the run was built from (05.2.2 §4).
        RunIdentityRules.Resolve(null, new CiRefClaim("refs/pull/12/merge", Sha1))
            .ShouldBe(new RunIdentity("pull_request", "12/merge", 12, null, null, null, false, false));
    }

    [Fact]
    public void Resolve_ClaimConflictingBody_ClaimWinsAndBodyContributesNothing()
    {
        // The body supplies every member and conflicts with the claim on kind, name, pr_number, head_sha and is_fork.
        var identity = RunIdentityRules.Resolve(
            Input("pull_request", "999/merge", 999, Sha2, Sha2, "push", isFork: true),
            new CiRefClaim("refs/heads/main", Sha1));

        // Verified members: the claim's. Body-only members: still the body's, and they mark the run unverified.
        identity.ShouldBe(new RunIdentity("branch", "main", null, Sha1, Sha2, "push", false, true));
    }

    [Fact]
    public void Resolve_PullRequestClaim_BodyHeadShaIsStoredAndUnverified()
    {
        RunIdentityRules.Resolve(
            Input("branch", "main", headSha: Sha1, baseSha: Sha2, triggerEvent: "pull_request"),
            new CiRefClaim("refs/pull/12/merge", Sha1))
            .ShouldBe(new RunIdentity("pull_request", "12/merge", 12, Sha1, Sha2, "pull_request", false, true));
    }

    [Fact]
    public void Resolve_UnparseableClaim_IdentityMembersStayNullAndTheBodyIsIgnoredForThem()
    {
        // A claim exists, so the caller is verified and kind/name/pr_number/is_fork are the claim's — which an
        // unrecognised ref makes null. head_sha has no verified value for an unknown ref shape, so the body's is
        // stored and labels the run unverified (the fail-toward-advisory direction).
        var identity = RunIdentityRules.Resolve(
            Input("tag", "v9", 7, Sha1, baseSha: Sha2, triggerEvent: "dispatch", isFork: true),
            new CiRefClaim("refs/notes/x", Sha1));

        identity.RefKind.ShouldBeNull();
        identity.RefName.ShouldBeNull();
        identity.PrNumber.ShouldBeNull();
        identity.IsFork.ShouldBeFalse();
        identity.HeadSha.ShouldBe(Sha1);
        identity.BaseSha.ShouldBe(Sha2);
        identity.TriggerEvent.ShouldBe("dispatch");
        identity.UnverifiedRef.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_BodyOnly_CarriesEveryMemberAndIsUnverified()
    {
        RunIdentityRules.Resolve(Input("tag", "v1.0.0", headSha: Sha1, baseSha: Sha2, triggerEvent: "manual", isFork: true), null)
            .ShouldBe(new RunIdentity("tag", "v1.0.0", null, Sha1, Sha2, "manual", true, true));
    }

    [Fact]
    public void Resolve_NoClaimNoRef_IsAllNullAndNotUnverified()
    {
        // Nothing was claimed and nothing was sent: the run records no identity at all, so there is nothing
        // unverified about it (AC2).
        RunIdentityRules.Resolve(null, null).ShouldBe(new RunIdentity(null, null, null, null, null, null, false, false));
        RunIdentityRules.Resolve(Input(), null).ShouldBe(new RunIdentity(null, null, null, null, null, null, false, false));
    }

    [Fact]
    public void Resolve_ClaimOnly_IsVerified()
    {
        RunIdentityRules.Resolve(null, new CiRefClaim("refs/heads/main", Sha1)).UnverifiedRef.ShouldBeFalse();
        RunIdentityRules.Resolve(null, new CiRefClaim("refs/tags/v1", Sha1)).UnverifiedRef.ShouldBeFalse();
    }

    [Theory]
    [InlineData("base_sha")]
    [InlineData("trigger_event")]
    public void Resolve_ClaimPlusOneBodyValue_IsUnverified(string member)
    {
        var body = member == "base_sha" ? Input(baseSha: Sha2) : Input(triggerEvent: "push");

        RunIdentityRules.Resolve(body, new CiRefClaim("refs/heads/main", Sha1)).UnverifiedRef.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ body shape (AC4)

    [Fact]
    public void ParseBodyRef_NoRefMember_IsNoBlockAndNoError()
    {
        var (block, errors) = RunIdentityRules.ParseBodyRef(Body("""{"manifest_id":"x"}"""));

        block.ShouldBeNull();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ParseBodyRef_ReadsEveryMember()
    {
        var (block, errors) = RunIdentityRules.ParseBodyRef(
            Body($$$"""{"ref":{"kind":"tag","name":"v1.0.0","pr_number":3,"head_sha":"{{{Sha1}}}","base_sha":"{{{Sha2}}}","trigger_event":"dispatch","is_fork":true}}"""));

        errors.ShouldBeEmpty();
        block.ShouldBe(Input("tag", "v1.0.0", 3, Sha1, Sha2, "dispatch", isFork: true));
    }

    [Fact]
    public void ParseBodyRef_NonObjectRef_IsInvalidValue()
    {
        foreach (var value in new[] { "\"branch\"", "3", "true", "[]", "null" })
        {
            var (block, errors) = RunIdentityRules.ParseBodyRef(Body($$$"""{"ref":{{{value}}}}"""));

            block.ShouldBeNull();
            errors.Single().Path.ShouldBe("/ref");
            errors.Single().Reason.ShouldBe("invalid_value");
        }
    }

    [Fact]
    public void ParseBodyRef_UnknownKindOrTriggerEvent_IsInvalidValue()
    {
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"kind":"commit"}}""")).Errors.Single().Path.ShouldBe("/ref/kind");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"trigger_event":"workflow_dispatch"}}""")).Errors.Single().Path.ShouldBe("/ref/trigger_event");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"kind":3}}""")).Errors.Single().Path.ShouldBe("/ref/kind");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"trigger_event":null}}""")).Errors.Single().Path.ShouldBe("/ref/trigger_event");
    }

    [Fact]
    public void ParseBodyRef_BadName_IsInvalidValue()
    {
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"name":""}}""")).Errors.Single().Path.ShouldBe("/ref/name");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"name":"   "}}""")).Errors.ShouldBeEmpty();
        RunIdentityRules.ParseBodyRef(Body($$$"""{"ref":{"name":"{{{new string('x', 256)}}}"}}""")).Errors.Single().Path.ShouldBe("/ref/name");
        RunIdentityRules.ParseBodyRef(Body($$$"""{"ref":{"name":"{{{new string('x', 255)}}}"}}""")).Errors.ShouldBeEmpty();
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"name":7}}""")).Errors.Single().Path.ShouldBe("/ref/name");
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    public void ParseBodyRef_NameWithAControlCharacter_IsInvalidValue(string name)
    {
        // C0 controls (newline, CR, tab), DEL and the C1 block (NEL included) are not ref names: refused invalid_value
        // like any other bad name, rather than stored (SEC-F3).
        var json = JsonSerializer.Serialize(new { @ref = new { name } });

        var (block, errors) = RunIdentityRules.ParseBodyRef(Body(json));

        block.ShouldBeNull();
        errors.Single().Path.ShouldBe("/ref/name");
        errors.Single().Reason.ShouldBe("invalid_value");
    }

    [Fact]
    public void ParseBodyRef_BadShas_AreInvalidValue()
    {
        foreach (var sha in new[] { new string('a', 39), new string('a', 41), new string('A', 40), new string('z', 40), "" })
        {
            RunIdentityRules.ParseBodyRef(Body($$$"""{"ref":{"head_sha":"{{{sha}}}"}}""")).Errors.Single().Path.ShouldBe("/ref/head_sha");
            RunIdentityRules.ParseBodyRef(Body($$$"""{"ref":{"base_sha":"{{{sha}}}"}}""")).Errors.Single().Path.ShouldBe("/ref/base_sha");
        }

        RunIdentityRules.ParseBodyRef(Body($$$"""{"ref":{"head_sha":"{{{Sha256}}}"}}""")).Errors.ShouldBeEmpty();
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"head_sha":42}}""")).Errors.Single().Path.ShouldBe("/ref/head_sha");
    }

    [Fact]
    public void ParseBodyRef_BadPrNumberOrIsFork_IsInvalidValue()
    {
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"pr_number":0}}""")).Errors.Single().Path.ShouldBe("/ref/pr_number");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"pr_number":-1}}""")).Errors.Single().Path.ShouldBe("/ref/pr_number");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"pr_number":1.5}}""")).Errors.Single().Path.ShouldBe("/ref/pr_number");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"pr_number":"12"}}""")).Errors.Single().Path.ShouldBe("/ref/pr_number");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"is_fork":"true"}}""")).Errors.Single().Path.ShouldBe("/ref/is_fork");
        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"is_fork":0}}""")).Errors.Single().Path.ShouldBe("/ref/is_fork");

        RunIdentityRules.ParseBodyRef(Body("""{"ref":{"pr_number":1,"is_fork":false}}""")).Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ParseBodyRef_UnknownMember_IsAdditionalPropertiesAndNamesTheMember()
    {
        var (block, errors) = RunIdentityRules.ParseBodyRef(Body("""{"ref":{"head_commit":"abc"}}"""));

        block.ShouldBeNull();
        errors.Single().Path.ShouldBe("/ref/head_commit");
        errors.Single().Reason.ShouldBe("additional_properties");
    }

    [Fact]
    public void ParseBodyRef_EveryBrokenMemberIsReported()
    {
        // The whole block is shape-validated in one pass, so the caller sees every offending member at once.
        var (block, errors) = RunIdentityRules.ParseBodyRef(Body("""{"ref":{"kind":"commit","pr_number":0,"is_fork":"x"}}"""));

        block.ShouldBeNull();
        errors.Select(error => error.Path).ShouldBe(["/ref/kind", "/ref/pr_number", "/ref/is_fork"]);
    }

    [Fact]
    public void EffectiveViolation_OnlyAPullRequestNeedsANumber()
    {
        // A verified claim produces kind and number together, so this rule can only be broken by a body-only caller.
        RunIdentityRules.EffectiveViolation(new RunIdentity("pull_request", null, null, null, null, null, false, true))
            .ShouldBe(new ErrorDetail("/ref/pr_number", "required"));
        RunIdentityRules.EffectiveViolation(new RunIdentity("pull_request", "12/merge", 12, null, null, null, false, true)).ShouldBeNull();
        RunIdentityRules.EffectiveViolation(new RunIdentity("branch", "main", null, Sha1, null, null, false, true)).ShouldBeNull();
        RunIdentityRules.EffectiveViolation(new RunIdentity(null, null, null, null, null, null, false, false)).ShouldBeNull();
    }
}

/// <summary>
/// 05.2.2-T1.4/AC9 at the command: the request identity the <c>Idempotency-Key</c> is hashed against now also covers the
/// caller's verified CI run reference, and a request without one hashes exactly as it did before this story (so no
/// stored record mass-conflicts on deploy).
/// </summary>
public sealed class CreateRunCommandCiBindingHashTests
{
    private static CreateRunCommand Command(string body, string? binding = null) =>
        new(Guid.CreateVersion7(), Encoding.UTF8.GetBytes(body), "key", binding);

    /// <summary>
    /// The plan §9 AC9 evidence <c>CreateRun_Replay_NoCiRunNoRef_HashUnchanged</c>: a request with neither a CI run
    /// reference nor a <c>ref</c> member hashes exactly as it did before 05.2.2, so no stored idempotency record
    /// mass-conflicts on deploy.
    /// </summary>
    [Fact]
    public void CreateRun_Replay_NoCiRunNoRef_HashUnchanged()
    {
        const string body = """{"manifest_id":"x","components":{"a":"1"}}""";
        var bytes = Encoding.UTF8.GetBytes(body);

        // The literal pre-change algorithm: the RFC 8785 digest of the body, nothing else.
        Command(body).BodyHash.ShouldBe(CanonicalHash.Compute(bytes));

        // Null and empty are both "no reference", so a member or ingest-token caller is unaffected.
        Command(body, string.Empty).BodyHash.ShouldBe(CanonicalHash.Compute(bytes));
        Command(body, null).BodyHash.ShouldBe(CanonicalHash.Compute(bytes));
    }

    [Fact]
    public void BodyHash_WithCiRunReference_FoldsBothHalvesIntoOneCanonicalDigest()
    {
        const string body = """{"manifest_id":"x"}""";
        var bodyHash = CanonicalHash.Compute(Encoding.UTF8.GetBytes(body));

        // Expected value built here by an independent path (one canonical hash of a two-member document), so the test
        // pins the layout rather than restating the implementation.
        var expected = CanonicalHash.Compute(
            $$$"""{"body_hash":{{{JsonSerializer.Serialize(bodyHash)}}},"ci_run_ref":{{{JsonSerializer.Serialize("987654321/42424242/3")}}}}""");

        Command(body, "987654321/42424242/3").BodyHash.ShouldBe(expected);
    }

    [Fact]
    public void BodyHash_DifferentCiRunReferenceOrBody_Differs()
    {
        const string body = """{"manifest_id":"x"}""";

        // A different run attempt of the repository is a different request identity.
        Command(body, "1/7/2").BodyHash.ShouldNotBe(Command(body, "1/7/3").BodyHash);
        Command(body, "1/7/2").BodyHash.ShouldNotBe(Command("""{"manifest_id":"y"}""", "1/7/2").BodyHash);
        Command(body, "1/7/2").BodyHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void BodyHash_UncanonicalizableBody_StillFallsBackToRawSha256OfItsOwnBytes()
    {
        // The reference is folded onto whatever the body hashes to, so the fallback path keeps its prefix (and a body
        // that cannot be canonicalized still reaches the handler, which answers its normal 400).
        Command("{ not json").BodyHash.ShouldStartWith("raw-sha256:");
        Command("{ not json", "1/7/2").BodyHash.ShouldStartWith("sha256:");
        Command("{ not json", "1/7/2").BodyHash.ShouldNotBe(Command("{ not json", "1/7/3").BodyHash);
    }
}

/// <summary>
/// 05.2.2-T1 AC1–AC4/AC9 through the real host and a real PostgreSQL: the identity a run stores and reports, the
/// verified <c>ci_token_issue</c> claim that wins over the body, the <c>unverified_ref</c> label, and the idempotency
/// key that now covers the caller's CI run reference.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CreateRunIdentityTests
{
    private readonly PostgresTenancyFixture _fixture;

    public CreateRunIdentityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);

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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(
            world.Database, world.Org, world.Project, "refs/heads/main", Sha, jti: "github-oidc-jwt-jti-42");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "minted-token-jti-9f2c1a"), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        result.Value.UnverifiedRef.ShouldBeFalse();
    }

    // ------------------------------------------------------------------ AC4: shape validation

    [Fact]
    public async Task CreateRun_InvalidRef_422RequestInvalidAndNoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        (string Ref, string Path)[] cases =
        [
            ("\"branch\"", "/ref"),
            ("3", "/ref"),
            ("null", "/ref"),
            ("""{"kind":"commit"}""", "/ref/kind"),
            ("""{"kind":3}""", "/ref/kind"),
            ("""{"trigger_event":"workflow_dispatch"}""", "/ref/trigger_event"),
            ("""{"name":""}""", "/ref/name"),
            ($$$"""{"name":"{{{new string('x', 256)}}}"}""", "/ref/name"),
            ($$$"""{"head_sha":"{{{new string('a', 41)}}}"}""", "/ref/head_sha"),
            ($$$"""{"base_sha":"{{{new string('A', 40)}}}"}""", "/ref/base_sha"),
            ("""{"head_sha":42}""", "/ref/head_sha"),
            ("""{"pr_number":0}""", "/ref/pr_number"),
            ("""{"pr_number":"12"}""", "/ref/pr_number"),
            ("""{"is_fork":"yes"}""", "/ref/is_fork"),
            ("""{"head_commit":"abc"}""", "/ref/head_commit"),
            // The only rule that runs on the effective values: a body-only pull_request without a number.
            ("""{"kind":"pull_request","name":"12/merge"}""", "/ref/pr_number"),
        ];

        foreach (var (refJson, path) in cases)
        {
            var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, refJson));

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refJson);
            answer.ErrorCode.ShouldBe("request_invalid", refJson);
            answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .Select(detail => detail.GetProperty("path").GetString())
                .ShouldContain(path, refJson);
        }

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    public async Task CreateRun_RefNameWithAControlCharacter_422RequestInvalidAndNoRows(string name)
    {
        // A git ref name cannot carry a C0/C1 control or DEL, so the body's ref_name is refused invalid_value like any
        // other bad name, before any write (SEC-F3). (NUL and a lone surrogate are refused even earlier, by the
        // unstorable-text pass; see CreateRunIdentityQaTests.)
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, JsonSerializer.Serialize(new { name })));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("path").GetString())
            .ShouldContain("/ref/name");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    [Fact]
    public async Task CreateRun_ValidBodyRef_IsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var body = BodyWithRef(
            world,
            $$$"""{"kind":"branch","name":"{{{new string('x', 255)}}}","pr_number":12,"head_sha":"{{{Sha}}}","base_sha":"{{{OtherSha}}}","trigger_event":"manual","is_fork":true}""");

        var answer = await RunCreationTests.CreateRunAsync(world, body);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();
    }

    // ------------------------------------------------------------------ AC9: idempotency identity

    [Fact]
    public async Task CreateRun_Replay_SameIdentity()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha);
        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);

        var first = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, Producer(world.Project, "token-replay"), body, key);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);

        // A re-minted token for the same run attempt carries a different (and, as in production, unrelated) jti and
        // must still verify and replay.
        var replay = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-replay-2"), body, key);

        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value.RunId.ShouldBe(first.Value.RunId);
        replay.Value.UnverifiedRef.ShouldBeFalse();
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.idempotency_record where operation = 'run:create' and org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateRun_Replay_DifferentIdentity_409()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await SeedClaimAsync(world.Database, world.Org, world.Project, "refs/heads/main", Sha, runAttempt: 1);
        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);

        var first = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-attempt", attempt: 1), body, key);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.UnverifiedRef.ShouldBeFalse();

        // The same key and the same body from a different run attempt of the repository is a different request
        // identity, so it must conflict rather than silently replay the first run (AC9).
        var conflict = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Producer(world.Project, "token-attempt", attempt: 2), body, key);

        conflict.IsSuccess.ShouldBeFalse();
        conflict.Error!.Code.ShouldBe(ApiErrorCode.IdempotencyConflict);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateRun_SameKeyDifferentBodyRef_409()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();

        var first = await RunCreationTests.CreateRunAsync(
            world,
            BodyWithRef(world, """{"kind":"branch","name":"main"}"""),
            idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        // The RFC 8785 body digest covers the ref block, so a different identity under the same key conflicts.
        var conflict = await RunCreationTests.CreateRunAsync(
            world,
            BodyWithRef(world, """{"kind":"branch","name":"release"}"""),
            idempotencyKey: key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateRun_ReplayedBodyRef_ReturnsTheSameUnverifiedLabel()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();
        var body = BodyWithRef(world, """{"kind":"branch","name":"main","trigger_event":"push"}""");

        var first = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();

        var replay = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("run_id").ShouldBe(first.Id("run_id"));
        replay.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue("the stored response is replayed verbatim");
    }
}
