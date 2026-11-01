using System.Text.Json.Nodes;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The wire shape of <c>GET /runs/{r}/decision</c>: one body for a pending and an evaluated run, whose
/// <c>allOf</c>/<c>if</c>/<c>then</c>/<c>else</c> makes every evaluation member null while pending and non-null once
/// evaluated. A shape that accepts either combination under both statuses is what these cases catch.
/// </summary>
/// <remarks>
/// Each negative instance differs from its valid control by one member, so a rejection names the member the schema
/// exists for rather than a malformed body. The pending cases are the ones the conditional owns; the others guard the
/// closed enumerations beside it.
/// </remarks>
public sealed class DecisionContractTests
{
    private const string SchemaName = "Decision";

    private const string SchemaUri = "urn:evidgate:contract-spec:" + SchemaName;

    /// <summary>Base URI of the narrowed copy: the validator reuses the compilation keyed by the spec entry's URI, so this copy needs its own.</summary>
    private const string MissingArrayBranchUri = "urn:evidgate:contract-spec-variant:decision-missing-array";

    /// <summary>A pending run: <c>status: pending</c>, every evaluation member null, and <c>url</c> still set.</summary>
    private const string PendingDecision = """
        {"status":"pending","decision":null,"evaluation_id":null,"evidence_revision":null,"completeness":null,"outcome":null,"missing":null,"reasons":null,"policy_hash":null,"manifest_hash":null,"evaluator_version":null,"mode":null,"url":"https://x/y"}
        """;

    /// <summary>An evaluated run whose evidence is complete: the closed <c>missing</c> list is empty, not null.</summary>
    private const string EvaluatedCompleteDecision = """
        {"status":"evaluated","decision":"PASS","evaluation_id":"018f0000-0000-7000-8000-000000000123","evidence_revision":7,"completeness":"complete","outcome":"all_passed","missing":[],"reasons":["policy:all_passed"],"policy_hash":"sha256:0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0","manifest_hash":"sha256:1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f809","evaluator_version":"evaluator-1.4.2","mode":"live","url":"https://x/y"}
        """;

    /// <summary>An evaluated run whose evidence is incomplete: two codes from the closed list.</summary>
    private const string EvaluatedIncompleteDecision = """
        {"status":"evaluated","decision":"INCONCLUSIVE","evaluation_id":"018f0000-0000-7000-8000-000000000124","evidence_revision":8,"completeness":"incomplete","outcome":"no_results","missing":["missing_shard","missing_result"],"reasons":[],"policy_hash":"sha256:1f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f1","manifest_hash":"sha256:2a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f802","evaluator_version":"evaluator-1.4.2","mode":"live","url":null}
        """;

    /// <summary>The eight codes the closed <c>missing</c> list may carry, in the order the contract lists them.</summary>
    private static readonly string[] MissingCodes =
    [
        "missing_shard",
        "count_mismatch",
        "artifact_missing",
        "build_mismatch",
        "unknown_status",
        "setup_error",
        "stale_epoch",
        "missing_result",
    ];

    private static readonly Lazy<OpenApiSpec> Spec = new(OpenApiSpec.Load);

    [Theory]
    [InlineData("a pending decision", PendingDecision)]
    [InlineData("an evaluated decision with complete evidence", EvaluatedCompleteDecision)]
    [InlineData("an evaluated decision with missing codes", EvaluatedIncompleteDecision)]
    public void Decision_WithItsOwnStatusMembers_Validates(string label, string instanceJson)
    {
        ContractSchema.AssertValid(Spec.Value, SchemaName, instanceJson, label);
    }

    [Fact]
    public void Pending_Decision_WithEmptyMissingArray_IsRejected()
    {
        var instance = With(PendingDecision, decision => decision["missing"] = new JsonArray());

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "a pending decision with 'missing: []'");
    }

    [Fact]
    public void Pending_Decision_WithDecisionPass_IsRejected()
    {
        var instance = With(PendingDecision, decision => decision["decision"] = "PASS");

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "a pending decision carrying 'decision: PASS'");
    }

    [Fact]
    public void Pending_Decision_WithEvaluationId_IsRejected()
    {
        var instance = With(
            PendingDecision,
            decision => decision["evaluation_id"] = "018f0000-0000-7000-8000-000000000123");

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "a pending decision carrying an 'evaluation_id'");
    }

    [Fact]
    public void Evaluated_Decision_WithNullDecision_IsRejected()
    {
        var instance = With(EvaluatedCompleteDecision, decision => decision["decision"] = null);

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "an evaluated decision with a null 'decision'");
    }

    [Fact]
    public void Evaluated_Decision_WithNullMissingArray_IsRejected()
    {
        var instance = With(EvaluatedCompleteDecision, decision => decision["missing"] = null);

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "an evaluated decision with a null 'missing'");
    }

    [Fact]
    public void Evaluated_Decision_WithShadowMode_IsRejected()
    {
        var instance = With(EvaluatedCompleteDecision, decision => decision["mode"] = "shadow");

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "an evaluated decision served from a shadow row");
    }

    /// <summary>
    /// A ninth code is outside the closed list. The member is validated under its array branch: the pinned validator
    /// skips an <c>items</c> subschema declared under a type union, and this instance's <c>missing</c> is an array, so
    /// the null branch cannot apply — every other keyword stays the spec entry's own.
    /// </summary>
    [Fact]
    public void Evaluated_Decision_WithNinthMissingCode_IsRejected()
    {
        var instance = With(EvaluatedIncompleteDecision, decision => decision["missing"] = new JsonArray("missing_events"));
        var schema = AsArrayBranch(Spec.Value.Schema(SchemaName), "missing");

        var failures = ContractSchema.Failures(schema.ToJsonString(), MissingArrayBranchUri, instance);

        Assert.True(
            failures.Length > 0,
            "an evaluated decision with a missing code outside the closed list must NOT validate against components.schemas.Decision: "
                + string.Join(" | ", failures));
    }

    [Fact]
    public void Pending_Decision_WithoutRequiredUrl_IsRejected()
    {
        var instance = With(PendingDecision, decision => decision.Remove("url"));

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "a pending decision without the required 'url'");
    }

    [Fact]
    public void Evaluated_Decision_WithoutRequiredManifestHash_IsRejected()
    {
        var instance = With(EvaluatedCompleteDecision, decision => decision.Remove("manifest_hash"));

        ContractSchema.AssertInvalid(Spec.Value, SchemaName, instance, "an evaluated decision without the required 'manifest_hash'");
    }

    /// <summary>
    /// The closed reason list the decision contract freezes: a gate reporting a code the contract does not declare, or
    /// missing one it does, is a wire break, so the enum is asserted as an exact set rather than a subset.
    /// </summary>
    [Fact]
    public void Decision_Missing_Items_Enum_Is_TheEightClosedCodes()
    {
        var missing = Spec.Value.Schema(SchemaName)["properties"]?["missing"] as JsonObject
            ?? throw new InvalidOperationException($"components.schemas.{SchemaName} declares no 'missing' property");
        var items = missing["items"] as JsonObject
            ?? throw new InvalidOperationException("components.schemas.Decision.properties.missing declares no 'items'");
        var declared = items["enum"] as JsonArray
            ?? throw new InvalidOperationException("components.schemas.Decision.properties.missing.items declares no 'enum'");

        var actual = declared.Select(code => code?.GetValue<string>()).Order(StringComparer.Ordinal);
        var expected = MissingCodes.Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    /// <summary>The instance with one member changed, so each negative case differs from its positive control by that member alone.</summary>
    private static string With(string instanceJson, Action<JsonObject> change)
    {
        var instance = JsonNode.Parse(instanceJson) as JsonObject
            ?? throw new InvalidOperationException("a decision instance must be a JSON object");
        change(instance);
        return instance.ToJsonString();
    }

    /// <summary>
    /// A copy of the spec entry whose named member keeps only its <c>array</c> type. Used where the instance proves
    /// the member is an array: the union's other branch cannot match it, so the narrowing adds no constraint the
    /// entry lacks, and it restores the <c>items</c> subschema the validator drops for a union type.
    /// </summary>
    private static JsonObject AsArrayBranch(JsonObject entry, string member)
    {
        var copy = JsonNode.Parse(entry.ToJsonString()) as JsonObject
            ?? throw new InvalidOperationException("a schema entry must be a JSON object");
        var property = copy["properties"]?[member] as JsonObject
            ?? throw new InvalidOperationException($"components.schemas.{SchemaName} declares no '{member}' property");
        property["type"] = "array";
        copy["$id"] = MissingArrayBranchUri;
        return copy;
    }
}
