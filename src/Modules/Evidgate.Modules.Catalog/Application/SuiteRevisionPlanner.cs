using Evidgate.BuildingBlocks.SharedKernel.Security;
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
/// <param name="Taxonomy">The taxonomy this revision is authored under (03.1.4).</param>
/// <param name="Warnings">Non-blocking observations.</param>
internal sealed record SuiteRevisionPlan(
    IReadOnlyList<PlannedMember> Members,
    IReadOnlyList<(int Parent, int Child)> Edges,
    string ShardJson,
    string RetryJson,
    string EnvironmentJson,
    string RevisionHash,
    SuiteTaxonomy Taxonomy,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates and canonicalises a suite-revision request (03.2.1-T1.2/T2/T3/T4): schemas for the three strategy
/// documents, secret-shape rules, variant normalisation, cross-project membership, the dependency DAG, and the
/// content hash. Read-only, so the create command and the dry-run preview share it and cannot disagree.
/// </summary>
internal sealed class SuiteRevisionPlanner(Lazy<ContractSchemaSet> schemaSet)
{
    internal const int MaxMembers = 10_000;
    internal const int MaxEdges = 50_000;

    /// <summary>The database refuses larger requirements (<c>suite_revision_json_check</c>); checked here so a row never reaches an error message.</summary>
    internal const int MaxEnvironmentBytes = 16_000;

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
    /// <param name="current">The taxonomy the suite holds today; a member of the request overrides it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<SuiteRevisionPlan>> PlanAsync(
        ITenantDb db,
        Guid projectId,
        CreateSuiteRevisionRequest request,
        SuiteTaxonomy current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        var members = request.Members ?? [];
        var edges = request.Edges ?? [];

        if (members.Any(member => member is null) || edges.Any(edge => edge is null))
        {
            return Fail(SuiteErrors.Invalid("members and edges cannot contain null", new ErrorDetail(members.Any(member => member is null) ? "/members" : "/edges", "null_element")));
        }

        if (members.Count > MaxMembers || edges.Count > MaxEdges)
        {
            return Fail(SuiteErrors.Invalid(
                $"a revision holds at most {MaxMembers} members and {MaxEdges} edges",
                new ErrorDetail(members.Count > MaxMembers ? "/members" : "/edges", "too_many")));
        }

        // ---- taxonomy (03.1.4 AC3, AC4): the closed lists, then the rule that decides what a mobile suite really is.
        var resolved = ResolveTaxonomy(request, current, members);
        if (!resolved.IsSuccess)
        {
            return Fail(resolved.Error!);
        }

        var taxonomy = resolved.Value;

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
                    [.. verdict.Errors.Take(20).Select(error => new ErrorDetail($"/{name}{SafePath(error)}", error.Reason))]));
            }
        }

        var sizes = new[] { shard.GetRawText(), retry.GetRawText(), environment.GetRawText() };
        if (System.Text.Encoding.UTF8.GetByteCount(sizes[2]) > MaxEnvironmentBytes)
        {
            return Fail(SuiteErrors.Invalid("environment_requirements is larger than 16000 bytes", new ErrorDetail("/environment_requirements", "too_large")));
        }

        var secretDetails = new List<ErrorDetail>();
        CheckEnvironment(environment, secretDetails);
        if (secretDetails.Count > 0)
        {
            return Fail(SuiteErrors.NotSecretFree(secretDetails));
        }

        // ---- members: refs, variants.
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<PlannedMember>(members.Count);
        var problems = new List<VariantProblem>();
        var seenVariants = new Dictionary<string, NormalizedVariant>(StringComparer.Ordinal);
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var reference = string.IsNullOrEmpty(member.Ref) ? "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : member.Ref;

            if (!IsValidRef(reference) || !refs.Add(reference))
            {
                return Fail(SuiteErrors.Invalid(
                    "member refs must be unique names of 1-64 letters, digits, '_', '.' or '-'",
                    new ErrorDetail($"/members/{index}/ref", refs.Contains(reference) ? "duplicate" : "invalid")));
            }

            if (member.CaseRevisionId == Guid.Empty)
            {
                return Fail(SuiteErrors.Invalid("a member needs a case_revision_id", new ErrorDetail($"/members/{index}/case_revision_id", "required")));
            }

            // Suites repeat a handful of parameter sets thousands of times: normalise each distinct text once.
            var rawParams = member.Params is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element ? element.GetRawText() : "{}";
            if (!seenVariants.TryGetValue(rawParams, out var variant))
            {
                variant = VariantNormalizer.Normalize(member.Params, $"/members/{index}/params", problems);
                if (variant is not null)
                {
                    seenVariants[rawParams] = variant;
                }
            }

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

        var edgeList = pairs.OrderBy(pair => ((long)pair.Item1 << 32) | (uint)pair.Item2).ToList();
        var graph = DependencyGraph.Analyse(ordered.Count, edgeList);
        if (graph.Cycle.Count > 0)
        {
            return Fail(SuiteErrors.Cycle([.. graph.Cycle.Take(50).Select((node, position) => new ErrorDetail($"/cycle/{position}", ordered[node].Ref))]));
        }

        var shardJson = sizes[0];
        var retryJson = sizes[1];
        var environmentJson = sizes[2];
        var warnings = new List<string>();
        if (ordered.Count == 0)
        {
            warnings.Add("the revision has no members");
        }

        if (taxonomy.Emulated)
        {
            warnings.Add("a mobile suite whose variants run a browser engine is stored as a web suite (device emulation)");
        }

        string revisionHash;
        try
        {
            revisionHash = Hash(ordered, edgeList, shardJson, retryJson, environmentJson, taxonomy.Taxonomy);
        }
        catch (JsonCanonicalizationException)
        {
            // A strategy document with duplicate member names, or a number RFC 8785 cannot represent.
            return Fail(SuiteErrors.Invalid("a strategy document cannot be canonicalised (duplicate member names or an unrepresentable number)", new ErrorDetail("/", "not_canonicalisable")));
        }

        return Result.Success(new SuiteRevisionPlan(ordered, edgeList, shardJson, retryJson, environmentJson, revisionHash, taxonomy.Taxonomy, warnings));
    }

    /// <summary>
    /// The taxonomy a revision is authored under: the request's values where it names them, the suite's current values
    /// otherwise, and the platform rule of AC4 on top. A request that names a platform but no level gets the level that
    /// platform defaults to, so <c>platform = api</c> alone is an integration suite.
    /// </summary>
    /// <param name="request">The authored revision.</param>
    /// <param name="current">The suite's current taxonomy.</param>
    /// <param name="members">The authored members, for the platform evidence their variants carry.</param>
    private static Result<(SuiteTaxonomy Taxonomy, bool Emulated)> ResolveTaxonomy(
        CreateSuiteRevisionRequest request,
        SuiteTaxonomy current,
        IReadOnlyList<SuiteMemberInput> members)
    {
        var platform = request.Platform is null ? current.Platform : Taxonomy.Canonical(request.Platform, Taxonomy.Platforms);
        if (platform is null)
        {
            return InvalidTaxonomy("platform", "platform must be one of web, api, unit or mobile");
        }

        var level = request.Level is null
            ? (request.Platform is null ? current.Level : Taxonomy.DefaultLevelFor(platform))
            : Taxonomy.Canonical(request.Level, Taxonomy.Levels);
        if (level is null)
        {
            return InvalidTaxonomy("level", "level must be one of unit, integration, e2e or smoke");
        }

        var purpose = request.Purpose is null ? current.Purpose : Taxonomy.Canonical(request.Purpose, Taxonomy.Purposes);
        if (purpose is null)
        {
            return InvalidTaxonomy("purpose", "purpose must be one of regression, smoke, release_gate or exploratory");
        }

        var browserDriven = members.Any(member => member is not null && Taxonomy.IsBrowserDriven(member.Params));
        var nativeMobile = members.Any(member => member is not null && Taxonomy.DeclaresNativeMobile(member.Params));
        var decision = Taxonomy.ResolvePlatform(platform, browserDriven, nativeMobile);
        if (decision.NeedsCapability)
        {
            return InvalidTaxonomy(
                "platform",
                "a mobile suite needs a member whose variant names a native mobile platform (android, ios); a browser variant is device emulation and belongs to a web suite",
                "mobile_capability_required");
        }

        return Result.Success((new SuiteTaxonomy(decision.Stored!, level, purpose), decision.Emulated));
    }

    private static Result<(SuiteTaxonomy Taxonomy, bool Emulated)> InvalidTaxonomy(string field, string message, string reason = "invalid_value") =>
        Result.Failure<(SuiteTaxonomy Taxonomy, bool Emulated)>(SuiteErrors.Invalid(message, [new ErrorDetail($"/{field}", reason)]));

    /// <summary>
    /// The content identity: the members in canonical order (case revision, required flag, variant hash), the edges as pairs of
    /// indexes into that order (the order is itself content, so the pairs are unambiguous and the document stays small), the
    /// three strategy documents and the taxonomy the revision is authored under. The suite id, row ids and ordinals are not
    /// part of it. <c>hash_input_version</c> names this shape: a revision stored before the taxonomy joined the input keeps
    /// the hash it was written with, and nothing ever recomputes it (03.1.4 AC7).
    /// </summary>
    internal static string Hash(
        IReadOnlyList<PlannedMember> members,
        IReadOnlyList<(int Parent, int Child)> edges,
        string shardJson,
        string retryJson,
        string environmentJson,
        SuiteTaxonomy taxonomy)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(members.Count * 160 + edges.Count * 14 + 512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("hash_input_version", 2);
            writer.WriteStartObject("taxonomy");
            writer.WriteString("platform", taxonomy.Platform);
            writer.WriteString("level", taxonomy.Level);
            writer.WriteString("purpose", taxonomy.Purpose);
            writer.WriteEndObject();
            writer.WriteStartArray("members");
            foreach (var member in members)
            {
                writer.WriteStartObject();
                writer.WriteString("case_revision_id", member.CaseRevisionId);
                writer.WriteBoolean("required", member.Required);
                writer.WriteString("variant_hash", member.Variant.Hash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("edges");
            foreach (var (parent, child) in edges)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(parent);
                writer.WriteNumberValue(child);
                writer.WriteEndArray();
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

        return CanonicalHash.Compute(buffer.WrittenSpan);
    }

    /// <summary>
    /// The secret-free rules of 03.2.1 AC8 over an environment document that already passed its schema. Typed, not a generic
    /// walk: a base URL must be a plain http(s) URL without userinfo, query or fragment (a credential hides in all three),
    /// map keys are held to the same key and value shapes as variant keys, and the names in <c>secrets[]</c> must look like
    /// names, not like the secrets themselves. A rejected key is never echoed.
    /// </summary>
    private static void CheckEnvironment(JsonElement environment, List<ErrorDetail> details)
    {
        const string root = "/environment_requirements";

        if (environment.TryGetProperty("base_urls", out var urls))
        {
            var position = 0;
            foreach (var entry in urls.EnumerateObject())
            {
                position++;
                CheckKey(entry.Name, $"{root}/base_urls/#{position}", details);
                var path = $"{root}/base_urls/#{position}";
                var text = SafeString(entry.Value);
                if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https")
                    || !string.IsNullOrEmpty(uri.UserInfo)
                    || !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment)
                    || text.Contains('@', StringComparison.Ordinal)
                    || !TextRules.IsClean(text))
                {
                    details.Add(new ErrorDetail(path, "secret_value"));
                }
                else if (SecretShape.ValueLooksSecret(text))
                {
                    details.Add(new ErrorDetail(path, "secret_value"));
                }
            }
        }

        if (environment.TryGetProperty("dependency_versions", out var versions))
        {
            var position = 0;
            foreach (var entry in versions.EnumerateObject())
            {
                position++;
                var path = $"{root}/dependency_versions/#{position}";
                CheckKey(entry.Name, path, details);
                var text = SafeString(entry.Value);
                if (text is null || !TextRules.IsClean(text) || SecretShape.ValueLooksSecret(text))
                {
                    details.Add(new ErrorDetail(path, "secret_value"));
                }
            }
        }

        if (environment.TryGetProperty("warm_up", out var warm) && warm.TryGetProperty("path", out var warmPath))
        {
            var text = SafeString(warmPath);
            if (text is null || !text.StartsWith('/') || text.AsSpan().IndexOfAny('?', '#', '@') >= 0 || !TextRules.IsClean(text) || SecretShape.ValueLooksSecret(text))
            {
                details.Add(new ErrorDetail($"{root}/warm_up/path", "secret_value"));
            }
        }

        if (environment.TryGetProperty("secrets", out var names))
        {
            var position = 0;
            foreach (var name in names.EnumerateArray())
            {
                var text = SafeString(name);
                if (text is null || SecretShape.SecretNameLooksLikeValue(text))
                {
                    details.Add(new ErrorDetail($"{root}/secrets/{position}", "secret_value"));
                }

                position++;
            }
        }
    }

    private static void CheckKey(string key, string path, List<ErrorDetail> details)
    {
        if (!TextRules.IsClean(key) || SecretShape.KeyLooksSecret(key) || SecretShape.ValueLooksSecret(key))
        {
            details.Add(new ErrorDetail(path, "secret_key"));
        }
    }

    private static string? SafeString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null; // an escaped lone surrogate
        }
    }

    /// <summary>A schema error path without the offending instance key: an <c>additionalProperties</c> violation names the key it refused, and that key may be the secret.</summary>
    private static string SafePath(ContractValidationError error)
    {
        if (!string.Equals(error.Reason, "additional_properties", StringComparison.Ordinal))
        {
            return TextRules.Brief(error.Path, 120);
        }

        var cut = error.Path.LastIndexOf('/');

        return cut <= 0 ? string.Empty : TextRules.Brief(error.Path[..cut], 120);
    }

    private static Result<SuiteRevisionPlan> Fail(Error error) => Result.Failure<SuiteRevisionPlan>(error);

    /// <summary>Member refs: 1-64 ASCII letters, digits, <c>_</c>, <c>.</c> or <c>-</c>.</summary>
    internal static bool IsValidRef(string reference) => AsciiShape.IsMatch(reference, 1, 64, AsciiShape.AlnumDotDash, AsciiShape.AlnumDotDash);
}
