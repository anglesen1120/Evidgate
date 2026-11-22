using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

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
