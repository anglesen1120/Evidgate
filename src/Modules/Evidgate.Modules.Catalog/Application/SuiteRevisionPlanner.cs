using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>A member after validation, with its variant resolved to a hash.</summary>
/// <param name="Ref">The author's name for the member.</param>
/// <param name="CaseRevisionId">The pinned case revision.</param>
/// <param name="Variant">Its normalised variant.</param>
/// <param name="Required">Whether it must pass.</param>
internal sealed record PlannedMember(string Ref, Guid CaseRevisionId, NormalizedVariant Variant, bool Required);

/// <summary>Everything a suite revision will store, computed without writing (03.2.1 AC1–AC4, AC8; preview, T5.2).</summary>
/// <param name="Members">Members in canonical order (case revision, then variant hash).</param>
/// <param name="Edges">Distinct edges as indexes into <paramref name="Members"/>.</param>
/// <param name="ShardJson">The shard strategy as stored.</param>
/// <param name="RetryJson">The retry policy as stored.</param>
/// <param name="EnvironmentJson">The environment requirements as stored.</param>
/// <param name="RevisionHash">The content identity.</param>
/// <param name="Warnings">Non-blocking observations.</param>
internal sealed record SuiteRevisionPlan(
    IReadOnlyList<PlannedMember> Members,
    IReadOnlyList<(int Parent, int Child)> Edges,
    string ShardJson,
    string RetryJson,
    string EnvironmentJson,
    string RevisionHash,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates and canonicalises a suite-revision request (03.2.1-T1.2/T2/T3/T4): schemas for the three strategy
/// documents, secret-shape rules, variant normalisation, cross-project membership, the dependency DAG, and the
/// content hash. Read-only, so the create command and the dry-run preview share it and cannot disagree.
/// </summary>
internal sealed partial class SuiteRevisionPlanner(Lazy<ContractSchemaSet> schemaSet)
{
    internal const int MaxMembers = 10_000;
    internal const int MaxEdges = 50_000;

    private const string ShardKey = "shard-strategy.v1";
    private const string RetryKey = "retry-policy.v1";
    private const string EnvironmentKey = "environment-requirements.v1";

    private const string KnownRevisionsSql = """
        select r.id
          from public.test_revision r
          join public.test_case c on c.org_id = r.org_id and c.id = r.case_id
         where r.id in (select t.value::uuid from jsonb_array_elements_text(@ids::jsonb) as t(value))
           and c.project_id = @project_id
        """;

    /// <summary>Plans <paramref name="request"/> for a suite of <paramref name="projectId"/>.</summary>
    /// <param name="db">The tenant read path.</param>
    /// <param name="projectId">The suite's project.</param>
    /// <param name="request">The authored revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<SuiteRevisionPlan>> PlanAsync(
        ITenantDb db,
        Guid projectId,
        CreateSuiteRevisionRequest request,
        CancellationToken cancellationToken)
    {
        var members = request.Members ?? [];
        var edges = request.Edges ?? [];

        if (members.Count > MaxMembers || edges.Count > MaxEdges)
        {
            return Fail(SuiteErrors.Invalid(
                $"a revision holds at most {MaxMembers} members and {MaxEdges} edges",
                new ErrorDetail(members.Count > MaxMembers ? "/members" : "/edges", "too_many")));
        }

        // ---- strategies: schema first, then the secret-shape rules on the environment requirements.
        var shard = request.ShardStrategy ?? JsonSerializer.Deserialize<JsonElement>("{\"kind\":\"none\"}");
        var retry = request.RetryPolicy ?? JsonSerializer.Deserialize<JsonElement>("{\"max_retries\":0}");
        var environment = request.EnvironmentRequirements ?? JsonSerializer.Deserialize<JsonElement>("{}");

        foreach (var (key, name, element) in new[] { (ShardKey, "shard_strategy", shard), (RetryKey, "retry_policy", retry), (EnvironmentKey, "environment_requirements", environment) })
        {
            var verdict = schemaSet.Value.Validate(key, element);
            if (!verdict.IsValid)
            {
                return Fail(SuiteErrors.Invalid(
                    $"{name} does not match {key}",
                    [.. verdict.Errors.Take(20).Select(error => new ErrorDetail($"/{name}{error.Path}", error.Reason))]));
            }
        }

        var secretDetails = new List<ErrorDetail>();
        CollectEnvironmentSecrets(environment, secretDetails);
        if (secretDetails.Count > 0)
        {
            return Fail(SuiteErrors.NotSecretFree(secretDetails));
        }

        // ---- members: refs, variants.
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<PlannedMember>(members.Count);
        var problems = new List<VariantProblem>();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var reference = string.IsNullOrEmpty(member.Ref) ? "m" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : member.Ref;

            if (!RefShape.IsMatch(reference) || !refs.Add(reference))
            {
                return Fail(SuiteErrors.Invalid(
                    "member refs must be unique names of 1-64 letters, digits, '_', '.' or '-'",
                    new ErrorDetail($"/members/{index}/ref", refs.Contains(reference) ? "duplicate" : "invalid")));
            }

            if (member.CaseRevisionId == Guid.Empty)
            {
                return Fail(SuiteErrors.Invalid("a member needs a case_revision_id", new ErrorDetail($"/members/{index}/case_revision_id", "required")));
            }

            var variant = VariantNormalizer.Normalize(member.Params, $"/members/{index}/params", problems);
            if (variant is not null)
            {
                planned.Add(new PlannedMember(reference, member.CaseRevisionId, variant, member.Required ?? true));
            }
        }

        if (problems.Count > 0)
        {
            var details = problems.Take(50).Select(problem => new ErrorDetail(problem.Path, problem.Reason)).ToList();

            return Fail(problems.Any(problem => problem.Reason.StartsWith("secret_", StringComparison.Ordinal))
                ? SuiteErrors.NotSecretFree(details)
                : SuiteErrors.Invalid("a member's params are not valid variant parameters", [.. details]));
        }

        var duplicate = planned.GroupBy(member => (member.CaseRevisionId, member.Variant.Hash)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return Fail(SuiteErrors.Invalid(
                "the same case revision with the same variant appears twice",
                [.. duplicate.Select(member => new ErrorDetail($"/members/{member.Ref}", "duplicate_member"))]));
        }

        // ---- membership in this project (the composite FK is only the cross-org backstop).
        if (planned.Count > 0)
        {
            var known = (await db.QueryAsync<Guid>(
                KnownRevisionsSql,
                new { ids = JsonSerializer.Serialize(planned.Select(member => member.CaseRevisionId).Distinct()), project_id = projectId },
                cancellationToken).ConfigureAwait(false)).ToHashSet();

            var unknown = planned.Where(member => !known.Contains(member.CaseRevisionId)).ToList();
            if (unknown.Count > 0)
            {
                return Fail(SuiteErrors.CrossProject([.. unknown.Take(50).Select(member => new ErrorDetail($"/members/{member.Ref}", "unknown_case_revision"))]));
            }
        }

        // ---- canonical order, edges, DAG.
        var ordered = planned.OrderBy(member => member.CaseRevisionId).ThenBy(member => member.Variant.Hash, StringComparer.Ordinal).ToList();
        var indexOf = ordered.Select((member, position) => (member.Ref, position)).ToDictionary(pair => pair.Ref, pair => pair.position, StringComparer.Ordinal);

        var pairs = new HashSet<(int, int)>();
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            if (edge.Parent is null || edge.Child is null || !indexOf.TryGetValue(edge.Parent, out var parent) || !indexOf.TryGetValue(edge.Child, out var child))
            {
                return Fail(SuiteErrors.Invalid("an edge names a member ref that does not exist", new ErrorDetail($"/edges/{index}", "unknown_ref")));
            }

            pairs.Add((parent, child));
        }

        var edgeList = pairs.OrderBy(pair => pair.Item1).ThenBy(pair => pair.Item2).ToList();
        var graph = DependencyGraph.Analyse(ordered.Count, edgeList);
        if (graph.Cycle.Count > 0)
        {
            return Fail(SuiteErrors.Cycle([.. graph.Cycle.Select((node, position) => new ErrorDetail($"/cycle/{position}", ordered[node].Ref))]));
        }

        var shardJson = shard.GetRawText();
        var retryJson = retry.GetRawText();
        var environmentJson = environment.GetRawText();
        var warnings = new List<string>();
        if (ordered.Count == 0)
        {
            warnings.Add("the revision has no members");
        }

        return Result.Success(new SuiteRevisionPlan(
            ordered,
            edgeList,
            shardJson,
            retryJson,
            environmentJson,
            Hash(ordered, edgeList, shardJson, retryJson, environmentJson),
            warnings));
    }

    /// <summary>The content identity: canonical members (sorted), edges (sorted) and the three strategy documents. Suite id and ordinals are not part of it.</summary>
    internal static string Hash(
        IReadOnlyList<PlannedMember> members,
        IReadOnlyList<(int Parent, int Child)> edges,
        string shardJson,
        string retryJson,
        string environmentJson)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("members");
            foreach (var member in members)
            {
                writer.WriteStartObject();
                writer.WriteString("case_revision_id", member.CaseRevisionId);
                writer.WriteString("variant_hash", member.Variant.Hash);
                writer.WriteBoolean("required", member.Required);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("edges");
            foreach (var (parent, child) in edges)
            {
                writer.WriteStartObject();
                writer.WriteStartObject("parent");
                writer.WriteString("case_revision_id", members[parent].CaseRevisionId);
                writer.WriteString("variant_hash", members[parent].Variant.Hash);
                writer.WriteEndObject();
                writer.WriteStartObject("child");
                writer.WriteString("case_revision_id", members[child].CaseRevisionId);
                writer.WriteString("variant_hash", members[child].Variant.Hash);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("shard_strategy");
            writer.WriteRawValue(shardJson);
            writer.WritePropertyName("retry_policy");
            writer.WriteRawValue(retryJson);
            writer.WritePropertyName("environment_requirements");
            writer.WriteRawValue(environmentJson);
            writer.WriteEndObject();
        }

        return CanonicalHash.Compute(stream.ToArray());
    }

    /// <summary>
    /// Values of the requirements must not look like secrets; <c>secrets[]</c> holds names and is exempt from the value rule
    /// (its shape is fixed by the schema), but map keys are checked like variant keys.
    /// </summary>
    private static void CollectEnvironmentSecrets(JsonElement environment, List<ErrorDetail> details)
    {
        foreach (var member in environment.EnumerateObject())
        {
            if (member.Name == "secrets")
            {
                continue;
            }

            Walk(member.Value, "/environment_requirements/" + member.Name, details);
        }
    }

    private static void Walk(JsonElement value, string path, List<ErrorDetail> details)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in value.EnumerateObject())
                {
                    var child = path + "/" + TextRules.Brief(member.Name, 64);
                    if (SecretShape.KeyLooksSecret(member.Name))
                    {
                        details.Add(new ErrorDetail(child, "secret_key"));
                    }

                    Walk(member.Value, child, details);
                }

                break;
            case JsonValueKind.String when SecretShape.ValueLooksSecret(value.GetString()!):
                details.Add(new ErrorDetail(path, "secret_value"));
                break;
        }
    }

    private static Result<SuiteRevisionPlan> Fail(Error error) => Result.Failure<SuiteRevisionPlan>(error);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex RefShapeRegex();

    private static System.Text.RegularExpressions.Regex RefShape => RefShapeRegex();
}
