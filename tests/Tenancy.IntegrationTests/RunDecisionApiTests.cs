// The route's whole surface through the real host: the newest live evaluation of the run, the pending body while it has
// none, the shadow rows it must never serve, and the statement budget of the read.
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// GET /v1/runs/{r}/decision through the real host: the run's newest <c>live</c> evaluation, or the pending body while
/// the run has none. The stored rows are arranged by the case rather than by the evaluator, because this route reads
/// what 07.2.2 wrote.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class RunDecisionApiTests
{
    /// <summary>The §5.1 members: the body always serializes every one of them.</summary>
    private const int BodyMembers = 13;

    /// <summary>The two code lists the incomplete verdict of <see cref="TwoLiveEvaluations_ServeTheNewestRevision"/> copies.</summary>
    private static readonly string[] ExpectedMissing = ["setup_error", "missing_result"];

    /// <summary>The reason codes that verdict carries.</summary>
    private static readonly string[] ExpectedReasons = ["setup_error"];

    /// <summary>
    /// The evaluated body copies the one live row of the run: status, decision, the evaluation's id, its evidence
    /// revision and every member of the verdict, with <c>mode</c> reporting the <c>live</c> slot it came from. The
    /// shadow row of the same run sits at a higher revision, so serving it would be visible here.
    /// </summary>
    [Fact]
    public async Task LiveEvaluation_ReturnsExactRow()
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
        var live = await InsertEvaluationAsync(world, runId, policy, new Verdict(2, "live", "PASS"));

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.EnumerateObject().Count().ShouldBe(BodyMembers, "every §5.1 member is always serialized");
        answer.Text("status").ShouldBe("evaluated");
        answer.Text("decision").ShouldBe("PASS");
        answer.Id("evaluation_id").ShouldBe(live);
        answer.Body.GetProperty("evidence_revision").GetInt32().ShouldBe(2, "the live row is the one at revision 2");
        answer.Text("completeness").ShouldBe("complete");
        answer.Text("outcome").ShouldBe("all_passed");
        answer.Text("policy_hash").ShouldBe(PolicyHash);
        answer.Text("manifest_hash").ShouldBe(ManifestHash);
        answer.Text("evaluator_version").ShouldBe("1.0.0");
        answer.Text("mode").ShouldBe("live");
        answer.Body.GetProperty("missing").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("reasons").GetArrayLength().ShouldBe(0);
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
    /// A run with no evaluation at all answers the pending body: <c>status</c> alone, every verdict member an explicit
    /// JSON null. <c>missing</c> must be null and not <c>[]</c>, because an empty gap list would claim the evaluator
    /// looked and found nothing, while the run has no verdict yet.
    /// </summary>
    [Fact]
    public async Task NoEvaluation_ReturnsPending()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.EnumerateObject().Count().ShouldBe(BodyMembers, "every §5.1 member is always serialized");
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
    /// A service principal without <c>decision:read</c> is refused 403 <c>forbidden</c> before any row is read, and the
    /// same run read as the org admin is the pending body — so the refusal is the permission and not the run.
    /// </summary>
    [Fact]
    public async Task NoPermission_403()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var denied = await GetAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["run:create"]));

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        (await GetAsync(world, runId)).Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A configured public base url turns the address into the project-relative run page, the link a UI shows for the
    /// decision; it is the deployment's origin, not the request's.
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
    /// 500 reason codes are served by the same single <c>evaluation</c> statement as one: the read is bounded by the
    /// row and never by its contents, so no verdict size can turn one request into a loop.
    /// </summary>
    [Fact]
    public async Task LargeReasonsArray_OneStatement()
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

        world.Api.Statements.Clear();
        var first = await GetAsync(world, runId);
        var smallStatements = world.Api.Statements.Snapshot();

        world.Api.Statements.Clear();
        var second = await GetAsync(world, largeRun);
        var largeStatements = world.Api.Statements.Snapshot();

        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Id("evaluation_id").ShouldBe(small);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Id("evaluation_id").ShouldBe(large);
        second.Body.GetProperty("reasons").GetArrayLength().ShouldBe(500);

        EvaluationStatements(smallStatements).ShouldBe(1, "one statement serves the row");
        EvaluationStatements(largeStatements).ShouldBe(1, "the size of reasons never adds a statement");
        largeStatements.Count.ShouldBe(smallStatements.Count, "the read's statement count is constant");
    }

    /// <summary>The captured statements that read the stored evaluation.</summary>
    private static int EvaluationStatements(IReadOnlyList<string> statements) =>
        statements.Count(statement => statement.Contains("from public.evaluation", StringComparison.OrdinalIgnoreCase));
}
