// The route's whole surface through the real host: the newest live evaluation of the run, the pending body while it has
// none, the shadow rows it must never serve, and the statement set of the read.
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// GET /v1/runs/{r}/decision through the real host: the run's newest <c>live</c> evaluation, or the pending body while the
/// run has none. The stored rows are arranged by the case rather than by the evaluator, because this route reads what the
/// evaluation command wrote.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class RunDecisionApiTests
{
    /// <summary>The members of the body: it always serializes every one of them.</summary>
    private const int BodyMembers = 13;

    /// <summary>The two code lists the incomplete verdict of <see cref="TwoLiveEvaluations_ServeTheNewestRevision"/> copies.</summary>
    private static readonly string[] ExpectedMissing = ["setup_error", "missing_result"];

    /// <summary>The reason codes that verdict carries.</summary>
    private static readonly string[] ExpectedReasons = ["setup_error"];

    /// <summary>
    /// The evaluated body copies the one live row of the run: status, decision, the evaluation's id, its evidence revision
    /// and every member of the verdict, with <c>mode</c> reporting the <c>live</c> slot it came from. The shadow row of the
    /// same run sits at a higher revision, so serving it would be visible here.
    /// </summary>
    [Theory]
    [InlineData("PASS", "complete", "all_passed", "", "")]
    [InlineData("FAIL", "incomplete", "has_failures", "missing_shard", "critical_not_executed")]
    [InlineData("INCONCLUSIVE", "incomplete", "no_results", "setup_error,missing_result", "setup_error")]
    public async Task LiveEvaluation_CopiesEveryMemberOfTheRow(
        string decision,
        string completeness,
        string outcome,
        string missing,
        string reasons)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world);
        await InsertEvaluationAsync(
            world,
            runId,
            policy,
            new Verdict(
                EvidenceRevision: 3,
                Mode: "shadow",
                Decision: "FAIL",
                Completeness: "incomplete",
                Outcome: "has_failures",
                Missing: ArrayLiteral(["missing_shard"]),
                Reasons: ArrayLiteral(["critical_not_executed"])));
        var live = await InsertEvaluationAsync(
            world,
            runId,
            policy,
            new Verdict(
                EvidenceRevision: 2,
                Mode: "live",
                Decision: decision,
                Completeness: completeness,
                Outcome: outcome,
                Missing: ArrayLiteral(Codes(missing)),
                Reasons: ArrayLiteral(Codes(reasons))));

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.EnumerateObject().Count().ShouldBe(BodyMembers, "every member of the body is always serialized");
        answer.Text("status").ShouldBe("evaluated");
        answer.Text("decision").ShouldBe(decision);
        answer.Id("evaluation_id").ShouldBe(live);
        answer.Body.GetProperty("evidence_revision").GetInt32().ShouldBe(2, "the live row is the one at revision 2");
        answer.Text("completeness").ShouldBe(completeness);
        answer.Text("outcome").ShouldBe(outcome);
        answer.Text("policy_hash").ShouldBe(PolicyHash);
        answer.Text("manifest_hash").ShouldBe(ManifestHash);
        answer.Text("evaluator_version").ShouldBe("1.0.0");
        answer.Text("mode").ShouldBe("live");
        answer.Body.GetProperty("missing").EnumerateArray().Select(code => code.GetString())
            .ShouldBe(Codes(missing));
        answer.Body.GetProperty("reasons").EnumerateArray().Select(code => code.GetString())
            .ShouldBe(Codes(reasons));
        answer.Body.GetProperty("url").ValueKind.ShouldBe(JsonValueKind.Null, "no public base url is configured");
    }

    /// <summary>
    /// Two live rows of one run (one per frozen evidence revision): the newest revision is the decision, and the
    /// incomplete verdict's two code lists are copied rather than normalised away.
    /// </summary>
    [Fact]
    public async Task TwoLiveEvaluations_ServeTheNewestRevision()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world);
        await InsertEvaluationAsync(world, runId, policy, new Verdict(1, "live", "PASS"));
        var newest = await InsertEvaluationAsync(
            world,
            runId,
            policy,
            new Verdict(
                EvidenceRevision: 3,
                Mode: "live",
                Decision: "INCONCLUSIVE",
                Completeness: "incomplete",
                Outcome: "no_results",
                Missing: ArrayLiteral(["setup_error", "missing_result"]),
                Reasons: ArrayLiteral(["setup_error"])));

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Id("evaluation_id").ShouldBe(newest);
        answer.Body.GetProperty("evidence_revision").GetInt32().ShouldBe(3);
        answer.Text("decision").ShouldBe("INCONCLUSIVE");
        answer.Text("completeness").ShouldBe("incomplete");
        answer.Text("outcome").ShouldBe("no_results");
        answer.Body.GetProperty("missing").EnumerateArray().Select(code => code.GetString())
            .ShouldBe(ExpectedMissing);
        answer.Body.GetProperty("reasons").EnumerateArray().Select(code => code.GetString())
            .ShouldBe(ExpectedReasons);
    }

    /// <summary>
    /// A run with no evaluation at all answers the pending body: <c>status</c> alone, every verdict member an explicit JSON
    /// null. <c>missing</c> must be null and not <c>[]</c>, because an empty gap list would claim the evaluator looked and
    /// found nothing while the run has no verdict yet.
    /// </summary>
    [Fact]
    public async Task NoEvaluation_ReturnsPending()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.EnumerateObject().Count().ShouldBe(BodyMembers, "every member of the body is always serialized");
        answer.Text("status").ShouldBe("pending");

        foreach (var member in new[]
                 {
                     "decision", "evaluation_id", "evidence_revision", "completeness", "outcome", "missing", "reasons",
                     "policy_hash", "manifest_hash", "evaluator_version", "mode", "url",
                 })
        {
            answer.Body.GetProperty(member).ValueKind.ShouldBe(JsonValueKind.Null, member);
        }

        answer.Body.GetProperty("missing").ValueKind.ShouldBe(
            JsonValueKind.Null, "a pending run has no gap list; [] would claim the evaluator ran");
    }

    /// <summary>
    /// Shadow rows are advice, never the served verdict: a run whose only stored evaluation is a shadow row is still
    /// pending, which is what the statement's <c>mode = 'live'</c> predicate holds.
    /// </summary>
    [Fact]
    public async Task ShadowOnly_ReturnsPending()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world);
        await InsertEvaluationAsync(
            world,
            runId,
            policy,
            new Verdict(
                EvidenceRevision: 1,
                Mode: "shadow",
                Decision: "FAIL",
                Completeness: "incomplete",
                Outcome: "has_failures",
                Missing: ArrayLiteral(["missing_shard"]),
                Reasons: ArrayLiteral(["setup_error"])));

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Text("status").ShouldBe("pending");
        answer.Body.GetProperty("decision").ValueKind.ShouldBe(JsonValueKind.Null, "a shadow verdict is never served");
        answer.Body.GetProperty("evaluation_id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A service credential without <c>decision:read</c> is refused 403 <c>forbidden</c> before any run is read, and the
    /// answer is the same for an existing run, an unknown id and another tenant's run — while the org admin reads the run,
    /// so the refusal is the permission and not the id.
    /// </summary>
    [Fact]
    public async Task NoPermission_403ForAnyRunId()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        await CreateProjectAsync(world.Client, otherUser, otherOrg, "rival-project");
        var foreign = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);

        var denied = TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["run:create"]);

        foreach (var id in new[] { runId, Guid.CreateVersion7(), foreign.RunId })
        {
            var answer = await GetAsync(world, id, denied);

            answer.Status.ShouldBe(HttpStatusCode.Forbidden);
            answer.ErrorCode.ShouldBe("forbidden");
        }

        (await GetAsync(world, runId)).Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A configured public base url turns the address into the run page, the link a UI shows for the decision; it is the
    /// deployment's origin, not the request's.
    /// </summary>
    [Fact]
    public async Task ConfiguredPublicBaseUrl_CarriesTheRunPage()
    {
        await using var world = await StartAsync(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Login:PublicBaseUrl"] = "https://app.evidgate.example",
            });
        var runId = await SeedRunAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Text("url").ShouldBe(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"https://app.evidgate.example/projects/{world.Project}/runs/{runId}"));
    }

    /// <summary>
    /// The read is the run's plan plus one row of its own <c>evaluation</c> table, whatever the verdict's size: 500 reason
    /// codes cost the same statement set as one, and an extra fixed statement would show up here.
    /// </summary>
    [Fact]
    public async Task ReasonsArraySize_DoesNotChangeTheStatementSet()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world);
        var small = await InsertEvaluationAsync(
            world,
            runId,
            policy,
            new Verdict(1, "live", "PASS", Reasons: ArrayLiteral(["expected_count_zero"])));
        var largeRun = await SeedRunAsync(world);
        var large = await InsertEvaluationAsync(
            world,
            largeRun,
            policy,
            new Verdict(
                EvidenceRevision: 1,
                Mode: "live",
                Decision: "FAIL",
                Completeness: "incomplete",
                Outcome: "has_failures",
                Missing: ArrayLiteral(["missing_shard"]),
                Reasons: ArrayLiteral(Enumerable.Repeat("expected_count_zero", 500))));

        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(world.Database));
        using var client = api.CreateClient();
        var caller = TenancyApiFactory.Caller(org: world.Org, project: world.Project, user: world.User);

        api.Statements.Clear();
        var first = await GetAsync(client, runId, caller);
        var smallStatements = WorkStatements(api.Statements.Snapshot());

        api.Statements.Clear();
        var second = await GetAsync(client, largeRun, caller);
        var largeStatements = WorkStatements(api.Statements.Snapshot());

        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Id("evaluation_id").ShouldBe(small);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Id("evaluation_id").ShouldBe(large);
        second.Text("decision").ShouldBe("FAIL");
        second.Body.GetProperty("reasons").GetArrayLength().ShouldBe(500);

        smallStatements.Count.ShouldBe(2, Join(smallStatements));
        smallStatements[0].ShouldContain("from public.run r");
        smallStatements[1].ShouldContain("from public.evaluation e");
        largeStatements.ShouldBe(smallStatements, "the size of reasons never changes the statement set");
    }

    /// <summary>The captured statements minus the tenant-scope setup, which every request emits.</summary>
    private static List<string> WorkStatements(IReadOnlyList<string> statements) =>
        [.. statements.Where(statement => !statement.Contains("set_config", StringComparison.Ordinal))];

    /// <summary>The captured statements as one line, for a failure message.</summary>
    private static string Join(IEnumerable<string> statements) => string.Join(" | ", statements);

    /// <summary>The codes of an inline-data list, spelled as a comma-separated list.</summary>
    private static string[] Codes(string codes) => codes.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
