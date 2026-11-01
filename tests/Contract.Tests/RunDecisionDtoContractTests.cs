using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Policy.Contracts;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The wire shape of <c>GET /v1/runs/{r}/decision</c> as the host actually serves it: the real
/// <see cref="Decision"/> record serialized with the host's snake_case policy validates against the spec schema, for a
/// pending and an evaluated run alike — so a member renamed, dropped, retyped or left out on either side fails here
/// rather than in a generated client.
/// </summary>
/// <remarks>
/// The instances are the real record (its <see cref="Decision.Pending"/> factory and its constructor), never a
/// hand-written literal, or the test would pin its own idea of the shape while the record drifts. The pending cases
/// are the ones the spec's <c>if</c>/<c>then</c>/<c>else</c> owns: a pending run must carry null in every
/// evaluation-derived member while <c>url</c> stays written. The negative control runs the other way — one member of
/// the serialized pending body is changed and the schema must reject it, or the valid cases prove nothing.
/// </remarks>
public sealed class RunDecisionDtoContractTests
{
    private const string SchemaName = "Decision";

    private static readonly Guid LiveEvaluation = Guid.Parse("018f0000-0000-7000-8000-000000000123");

    private const string RunPage = "https://evidgate.example/v1/runs/018f0000-0000-7000-8000-0000000000aa";

    /// <summary>A run with no live evaluation row: the dropped-verdict body the schema's <c>then</c> branch owns.</summary>
    [Fact]
    public void Pending_Dto_ValidatesAgainstTheSpecSchema()
    {
        var json = JsonSerializer.Serialize(Decision.Pending(RunPage), SnakeCase);

        // Every member the schema requires is written, the evaluation ones as explicit nulls: an absent member would
        // read as a malformed answer, and the schema requires all thirteen.
        json.ShouldContain("\"status\":\"pending\"", Case.Sensitive);
        json.ShouldContain("\"decision\":null", Case.Sensitive);
        json.ShouldContain("\"missing\":null", Case.Sensitive);
        json.ShouldContain($"\"url\":\"{RunPage}\"", Case.Sensitive);

        ContractSchema.AssertValid(OpenApiSpec.Load(), SchemaName, json, "a pending run-decision body");
    }

    /// <summary>
    /// An evaluated run: every member populated, from the record's own constructor. The renamed
    /// <see cref="Decision.DecisionValue"/> member keeps its wire name <c>decision</c>.
    /// </summary>
    /// <remarks>
    /// The verdict and the code lists are the row's own values copied verbatim, so this case pins that a fully
    /// populated body validates — which verdict pairs with which codes is the evaluator's contract, not the wire's.
    /// </remarks>
    [Fact]
    public void Evaluated_Dto_ValidatesAgainstTheSpecSchema()
    {
        var evaluated = new Decision(
            DecisionStatus.Evaluated,
            "PASS",
            LiveEvaluation,
            7,
            "incomplete",
            "has_failures",
            ["missing_shard", "missing_result"],
            ["policy:all_passed", "policy:missing_evidence"],
            "sha256:0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0",
            "sha256:1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f809",
            "evaluator-1.4.2",
            "live",
            RunPage);

        var json = JsonSerializer.Serialize(evaluated, SnakeCase);

        // The property name of the record member is not the wire name: only the JSON tells them apart.
        json.ShouldContain("\"decision\":\"PASS\"", Case.Sensitive);
        json.ShouldContain("\"missing\":[\"missing_shard\",\"missing_result\"]", Case.Sensitive);
        json.ShouldContain("\"mode\":\"live\"", Case.Sensitive);

        ContractSchema.AssertValid(OpenApiSpec.Load(), SchemaName, json, "an evaluated run-decision body");
    }

    /// <summary>
    /// The evaluated body with no run page to link: <c>url</c> is a required member the deployment may leave null,
    /// so the schema must accept the null rather than a body that omits it.
    /// </summary>
    [Fact]
    public void Evaluated_Dto_WithNullUrl_ValidatesAgainstTheSpecSchema()
    {
        var evaluated = new Decision(
            DecisionStatus.Evaluated,
            "INCONCLUSIVE",
            LiveEvaluation,
            8,
            "incomplete",
            "no_results",
            ["missing_shard"],
            [],
            "sha256:1f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f1",
            "sha256:2a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f802",
            "evaluator-1.4.2",
            "live",
            null);

        var json = JsonSerializer.Serialize(evaluated, SnakeCase);

        json.ShouldContain("\"url\":null", Case.Sensitive);
        json.ShouldContain("\"reasons\":[]", Case.Sensitive);

        ContractSchema.AssertValid(OpenApiSpec.Load(), SchemaName, json, "an evaluated run-decision body without a run page");
    }

    /// <summary>
    /// The negative control: the serialized pending body with <c>status</c> alone changed to <c>evaluated</c> must be
    /// rejected — the spec's <c>else</c> branch then requires a verdict, an evaluation id and the rest, so the valid
    /// cases above are checked against a live schema rather than a document that accepts everything.
    /// </summary>
    [Fact]
    public void Pending_Dto_ClaimingEvaluated_IsRejected()
    {
        var pending = Decision.Pending(RunPage);
        var instance = JsonNode.Parse(JsonSerializer.Serialize(pending, SnakeCase))!.AsObject();
        instance["status"] = DecisionStatus.Evaluated;

        ContractSchema.AssertInvalid(
            OpenApiSpec.Load(),
            SchemaName,
            instance.ToJsonString(),
            "a pending run-decision body whose status claims 'evaluated' while every evaluation member is null");
    }

    /// <summary>The host's wire policy (<c>ApiHost</c>): snake_case, nulls written.</summary>
    private static JsonSerializerOptions SnakeCase { get; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
