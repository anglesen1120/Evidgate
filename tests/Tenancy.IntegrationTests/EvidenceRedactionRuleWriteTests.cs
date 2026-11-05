// The shape and the boundaries of the `redaction_rules` member on the kind-policy write: a null entry, a member a rule
// does not declare, a replacement that must not accumulate, and the 64/65 and 1/128/129 edges with their audit counts.
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceCaptureOptionsTests
{
    /// <summary>One rule object of a body, as JSON.</summary>
    private static string Rule(string ruleType, string namePattern) =>
        $$"""{"rule_type":"{{ruleType}}","name_pattern":"{{namePattern}}"}""";

    /// <summary>A rules member of <paramref name="count"/> header rules, each named by <paramref name="name"/>.</summary>
    private static string Rules(int count, Func<int, string> name) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(index => Rule("header", name(index)))) + "]";

    [Fact]
    public async Task CaptureOptions_NullRuleEntry_422AndNoRows()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[null,{"rule_type":"header","name_pattern":"authorization"}]"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await RuleRowsAsync(world)).ShouldBeEmpty();
        (await PolicyRowsAsync(world)).ShouldBeEmpty("the refusal lands before the policy row is written");
        (await AuditCountsAsync(world)).ShouldBeEmpty("a refused write appends no audit row");
    }

    [Fact]
    public async Task CaptureOptions_UnknownRuleMember_400AndNoRows()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        // A rule object is closed: `additionalProperties: false` in the contract, enforced at binding.
        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[{"rule_type":"header","name_pattern":"authorization","path":"/x"}]"""));

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await RuleRowsAsync(world)).ShouldBeEmpty();
        (await PolicyRowsAsync(world)).ShouldBeEmpty();
        (await AuditCountsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureOptions_ReplacementAndReplay_DoNotAccumulate()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var declared = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: $"[{Rule("header", "authorization")},{Rule("cookie", "session*")}]"));
        declared.Status.ShouldBe(HttpStatusCode.OK, declared.ErrorMessage);

        var replacement = PolicyBody(rules: $"[{Rule("query_param", "token")}]");
        var replaced = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller, replacement);
        replaced.Status.ShouldBe(HttpStatusCode.OK, replaced.ErrorMessage);
        replaced.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(1);
        (await RuleRowsAsync(world)).ShouldBe(["trace|query_param|token"], "a replacement leaves none of the old set");

        // The same body again is the same set: one declaration is one row, and the count never grows.
        var replayed = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller, replacement);
        replayed.Status.ShouldBe(HttpStatusCode.OK, replayed.ErrorMessage);
        replayed.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(1);
        (await RuleRowsAsync(world)).ShouldBe(["trace|query_param|token"]);

        // The chain counts what each write replaced and what it left: two, then one, then one again.
        (await AuditCountsAsync(world)).ShouldBe(["0|2", "2|1", "1|1"]);
    }

    [Fact]
    public async Task CaptureOptions_RuleBoundaries_CountAndPatternLength()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var atLimit = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: Rules(64, index => $"x{index}")));
        atLimit.Status.ShouldBe(HttpStatusCode.OK, atLimit.ErrorMessage);
        atLimit.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(64);
        (await RuleRowsAsync(world)).Count.ShouldBe(64);
        (await AuditCountsAsync(world)).ShouldBe(["0|64"]);

        var overLimit = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: Rules(65, index => $"y{index}")));
        overLimit.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overLimit.ErrorCode.ShouldBe("request_invalid");
        (await RuleRowsAsync(world)).Count.ShouldBe(64, "a refused write replaces nothing");
        (await AuditCountsAsync(world)).ShouldBe(["0|64"], "and appends no audit row");

        var shortest = new string('a', 1);
        (await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: $"[{Rule("header", shortest)}]"))).Status.ShouldBe(HttpStatusCode.OK);
        (await RuleRowsAsync(world)).ShouldBe([$"trace|header|{shortest}"]);

        var longest = new string('a', 128);
        (await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: $"[{Rule("header", longest)}]"))).Status.ShouldBe(HttpStatusCode.OK);
        (await RuleRowsAsync(world)).ShouldBe([$"trace|header|{longest}"]);

        var tooLong = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: $"[{Rule("header", new string('a', 129))}]"));
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        tooLong.ErrorCode.ShouldBe("request_invalid");
        (await RuleRowsAsync(world)).ShouldBe([$"trace|header|{longest}"], "the longest stored name survives the refusal");
        (await AuditCountsAsync(world)).ShouldBe(["0|64", "64|1", "1|1"]);
    }

    /// <summary>
    /// The chain's rule counts of this project's kind-policy writes, oldest first, as
    /// <c>before|after</c>; the two counts exist only when the write sent the rules member.
    /// </summary>
    private Task<IReadOnlyList<string>> AuditCountsAsync(World world, string kind = "trace") =>
        _fixture.ColumnAsync<string>(
            world.Database,
            "select coalesce(metadata->>'redaction_rule_count_before', '-') || '|' "
            + "|| coalesce(metadata->>'redaction_rule_count', '-') from public.audit_log "
            + "where action = 'artifact_kind_policy.set' "
            + $"and org_id = '{world.Org:D}' and object_id = '{world.Project:D}:{kind}' order by seq");
}
