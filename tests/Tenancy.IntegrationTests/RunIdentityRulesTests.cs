using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1.3/AC1–AC4 as pure functions: the body <c>ref</c> shape rules, the body-only identity with its
/// <c>unverified_ref</c> label and the effective-value rule — all without a database, so a trust-policy change
/// fails here before it can reach the served route.
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

    // ------------------------------------------------------------------ declared identity (AC1–AC3)

    [Fact]
    public void Resolve_BodyOnly_CarriesEveryMemberAndIsUnverified()
    {
        RunIdentityRules.Resolve(Input("tag", "v1.0.0", headSha: Sha1, baseSha: Sha2, triggerEvent: "manual", isFork: true))
            .ShouldBe(new RunIdentity("tag", "v1.0.0", null, Sha1, Sha2, "manual", true, true));
    }

    [Fact]
    public void Resolve_NoRef_IsAllNullAndNotUnverified()
    {
        // Nothing was sent: the run records no identity at all, so there is nothing declared about it (AC2).
        RunIdentityRules.Resolve(null).ShouldBe(new RunIdentity(null, null, null, null, null, null, false, false));
        RunIdentityRules.Resolve(Input()).ShouldBe(new RunIdentity(null, null, null, null, null, null, false, false));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("name")]
    [InlineData("pr_number")]
    [InlineData("head_sha")]
    [InlineData("base_sha")]
    [InlineData("trigger_event")]
    [InlineData("is_fork")]
    public void Resolve_AnySingleSuppliedMember_IsUnverified(string member)
    {
        // Every member is declared by the producer and none can be verified, so supplying any one labels the run.
        var body = member switch
        {
            "kind" => Input(kind: "branch"),
            "name" => Input(name: "main"),
            "pr_number" => Input(prNumber: 4),
            "head_sha" => Input(headSha: Sha1),
            "base_sha" => Input(baseSha: Sha2),
            "trigger_event" => Input(triggerEvent: "push"),
            _ => Input(isFork: true),
        };

        RunIdentityRules.Resolve(body).UnverifiedRef.ShouldBeTrue(member);
    }

    [Fact]
    public void Resolve_PullRequestHeadSha_IsStoredAsDeclared()
    {
        // No claim can replace the head commit, so the producer's own head_sha is what the run reports.
        RunIdentityRules.Resolve(Input("pull_request", "12/merge", 12, Sha1))
            .ShouldBe(new RunIdentity("pull_request", "12/merge", 12, Sha1, null, null, false, true));
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
        // Every member is the producer's, so a pull_request without a number can only come from the body.
        RunIdentityRules.EffectiveViolation(new RunIdentity("pull_request", null, null, null, null, null, false, true))
            .ShouldBe(new ErrorDetail("/ref/pr_number", "required"));
        RunIdentityRules.EffectiveViolation(new RunIdentity("pull_request", "12/merge", 12, null, null, null, false, true)).ShouldBeNull();
        RunIdentityRules.EffectiveViolation(new RunIdentity("branch", "main", null, Sha1, null, null, false, true)).ShouldBeNull();
        RunIdentityRules.EffectiveViolation(new RunIdentity(null, null, null, null, null, null, false, false)).ShouldBeNull();
    }
}
