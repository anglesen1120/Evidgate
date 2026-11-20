using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>What a schema-valid manifest declares, once its semantic checks pass (05.1.1-T2).</summary>
/// <param name="SuiteRevisionId">The suite revision the plan executes.</param>
/// <param name="EnvironmentRevisionId">The environment revision it is pinned to, if any.</param>
/// <param name="PlannedCount">Number of planned executions.</param>
/// <param name="CaseRevisionIds">Every case revision the matrix names, in matrix order.</param>
/// <param name="PlannedCaseRevisionIds">The case revision of each planned execution, by index.</param>
internal sealed record ManifestFacts(
    Guid SuiteRevisionId,
    Guid? EnvironmentRevisionId,
    int PlannedCount,
    IReadOnlyList<Guid> CaseRevisionIds,
    IReadOnlyList<Guid> PlannedCaseRevisionIds);

/// <summary>A refused manifest: the 05.1.1 reason and its indexed details (never a submitted value).</summary>
/// <param name="Reason"><c>secret_in_variant_params</c>, <c>invalid_value</c> or <c>matrix_not_covered</c>.</param>
/// <param name="Details">At most <see cref="ManifestVerifier.MaxDetails"/> rows.</param>
internal sealed record ManifestRefusal(string Reason, IReadOnlyList<ErrorDetail> Details);

/// <summary>
/// The pure semantic checks of a manifest that already matches <c>manifest.v1.json</c> (05.1.1-T2.3/T2.4): no secret
/// literal in any variant's parameters, and a planned-execution list and shard plan that cover the declared matrix
/// exactly. The database checks (unknown case/suite/environment revision) are the handler's.
/// </summary>
internal static class ManifestVerifier
{
    /// <summary>Most details one refusal carries (the envelope stays small whatever the manifest's size).</summary>
    internal const int MaxDetails = 50;

    internal const string SecretInVariantParams = "secret_in_variant_params";
    internal const string MatrixNotCovered = "matrix_not_covered";
    internal const string InvalidValue = "invalid_value";
    internal const string SecretValueDetected = "secret_value_detected";

    /// <summary>Checks <paramref name="root"/>; exactly one of the two results is non-null.</summary>
    /// <param name="root">A document the manifest schema accepted.</param>
    internal static (ManifestFacts? Facts, ManifestRefusal? Refusal) Verify(JsonElement root)
    {
        var planned = root.GetProperty("planned_executions");
        var matrix = root.GetProperty("matrix");

        var secrets = SecretDetails(planned, matrix, out var unclean);
        SecretRefDetails(root, secrets, unclean);

        // Secret refusals fill the detail budget first and name the reason; unclean text only adds to what is left.
        secrets.AddRange(unclean.Take(Math.Max(0, MaxDetails - secrets.Count)));
        if (secrets.Count > 0)
        {
            return (null, new ManifestRefusal(secrets[0].Reason, secrets));
        }

        var coverage = CoverageDetails(root, planned, matrix, out var caseIds, out var plannedCases);
        if (coverage.Count > 0)
        {
            return (null, new ManifestRefusal(MatrixNotCovered, coverage));
        }

        var environment = root.TryGetProperty("environment_revision_id", out var env) && env.ValueKind == JsonValueKind.String
            ? env.GetGuid()
            : (Guid?)null;

        return (new ManifestFacts(root.GetProperty("suite_revision_id").GetGuid(), environment, plannedCases.Count, caseIds, plannedCases), null);
    }

    /// <summary>
    /// Every variant parameter that is, or is named like, a secret. A <c>secret:&lt;name&gt;</c> value is a reference and is
    /// allowed under any key; a parameter is named by its position (<c>#n</c>), so neither its value nor its key is echoed.
    /// Identical parameter objects are checked once (a 10 000-row plan repeats a handful of variants).
    /// </summary>
    private static List<ErrorDetail> SecretDetails(JsonElement planned, JsonElement matrix, out List<ErrorDetail> unclean)
    {
        var details = new List<ErrorDetail>();
        var cleanliness = new List<ErrorDetail>();
        var memo = new Dictionary<string, int[]>(StringComparer.Ordinal);

        var index = 0;
        foreach (var execution in planned.EnumerateArray())
        {
            Add(details, cleanliness, memo, execution.GetProperty("variant").GetProperty("params"), $"/planned_executions/{index}/variant/params", index);
            index++;
        }

        var variant = 0;
        foreach (var entry in matrix.GetProperty("variants").EnumerateArray())
        {
            Add(details, cleanliness, memo, entry.GetProperty("params"), $"/matrix/variants/{variant}/params", null);
            variant++;
        }

        unclean = cleanliness;

        return details;

        static void Add(List<ErrorDetail> details, List<ErrorDetail> cleanliness, Dictionary<string, int[]> memo, JsonElement parameters, string path, int? index)
        {
            var raw = parameters.GetRawText();
            if (!memo.TryGetValue(raw, out var positions))
            {
                // A secret refusal keeps precedence wherever it fires; unclean text is reported only for the rest (negative positions).
                var secret = SecretPositions(parameters);
                positions = [.. secret, .. UncleanPositions(parameters).Where(p => !secret.Contains(p)).Select(p => -p)];
                positions = [.. positions.OrderBy(Math.Abs)];
                memo[raw] = positions;
            }

            foreach (var position in positions)
            {
                var target = position < 0 ? cleanliness : details;
                if (target.Count >= MaxDetails)
                {
                    continue;
                }

                var reason = position < 0 ? InvalidValue : SecretInVariantParams;
                target.Add(new ErrorDetail(string.Create(CultureInfo.InvariantCulture, $"{path}/#{Math.Abs(position)}"), reason, index));
            }
        }
    }

    /// <summary>
    /// The other free text of a manifest that must not carry a secret: <c>env_needs</c> references (the schema fixes their
    /// shape, not whether the name is a token) and shard <c>producer_hint</c>s.
    /// </summary>
    private static void SecretRefDetails(JsonElement root, List<ErrorDetail> details, List<ErrorDetail> unclean)
    {
        var need = 0;
        foreach (var entry in root.GetProperty("env_needs").GetProperty("target_envs").EnumerateArray())
        {
            if (entry.TryGetProperty("secret_refs", out var refs))
            {
                var position = 0;
                foreach (var reference in refs.EnumerateArray())
                {
                    if (details.Count < MaxDetails && IsSecret("ref", reference.GetString()!))
                    {
                        details.Add(new ErrorDetail(string.Create(CultureInfo.InvariantCulture, $"/env_needs/target_envs/{need}/secret_refs/{position}"), SecretValueDetected));
                    }

                    position++;
                }
            }

            need++;
        }

        var shard = 0;
        foreach (var entry in root.GetProperty("shard_plan").GetProperty("shards").EnumerateArray())
        {
            if (entry.TryGetProperty("producer_hint", out var hint))
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"/shard_plan/shards/{shard}/producer_hint");
                var text = hint.GetString()!;

                // A hint is never a variant identity, so unclean text is refused outright; a token hidden by it keeps the secret reason.
                if (UnicodeCleanliness.IsClean(text))
                {
                    if (details.Count < MaxDetails && SecretShape.ValueLooksSecret(text))
                    {
                        details.Add(new ErrorDetail(path, SecretValueDetected));
                    }
                }
                else if (SecretShape.ValueLooksSecret(UnicodeCleanliness.StripInvisible(text)))
                {
                    if (details.Count < MaxDetails)
                    {
                        details.Add(new ErrorDetail(path, SecretValueDetected));
                    }
                }
                else if (unclean.Count < MaxDetails)
                {
                    unclean.Add(new ErrorDetail(path, InvalidValue));
                }
            }

            shard++;
        }
    }

    /// <summary>
    /// The 1-based positions of the parameters whose name, or whose string value under an identifying key, is not clean text
    /// (control, format or invisible characters: the normaliser's rule); never echoed. Free-text keys are exempt, as in the
    /// normaliser, and are covered by the stripped secret scan instead.
    /// </summary>
    internal static int[] UncleanPositions(JsonElement parameters)
    {
        List<int>? found = null;
        var position = 0;
        foreach (var member in parameters.EnumerateObject())
        {
            position++;
            if (MemberIsUnclean(member))
            {
                (found ??= []).Add(position);
            }
        }

        return found?.ToArray() ?? [];
    }

    private static bool MemberIsUnclean(JsonProperty member)
    {
        try
        {
            // A free-text key may carry any clean-or-not text (a ZWJ emoji, ZWNJ script); only its stripped form is scanned.
            return !UnicodeCleanliness.IsClean(member.Name)
                || (member.Value.ValueKind == JsonValueKind.String
                    && !ParameterSecretRule.IsNonIdentifying(member.Name)
                    && !UnicodeCleanliness.IsClean(member.Value.GetString()));
        }
        catch (InvalidOperationException)
        {
            // Unreadable text is refused by SecretPositions; not counted twice.
            return false;
        }
    }

    /// <summary>The 1-based positions of the secret-shaped parameters of one params object; an unreadable name counts as one.</summary>
    internal static int[] SecretPositions(JsonElement parameters)
    {
        List<int>? found = null;
        var position = 0;
        foreach (var member in parameters.EnumerateObject())
        {
            position++;
            if (MemberIsSecret(member))
            {
                (found ??= []).Add(position);
            }
        }

        return found?.ToArray() ?? [];
    }

    /// <summary>True when the pair carries a secret literal: a reference (<c>secret:name</c>) never does.</summary>
    internal static bool IsSecret(string key, string value)
    {
        if (value.StartsWith("secret:", StringComparison.Ordinal))
        {
            return ParameterSecretRule.ReferenceLooksSecret(value);
        }

        return SecretShape.KeyLooksSecret(key) || SecretShape.ValueLooksSecret(key) || ParameterSecretRule.ValueLooksSecret(key, value)
            || ParameterSecretRule.HiddenSecret(key, value);
    }

    private static bool MemberIsSecret(JsonProperty member)
    {
        string name;
        string value;
        try
        {
            name = member.Name;
            value = member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString()! : member.Value.GetRawText();
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate cannot be read: refused by position, never echoed. The upload path rejects it earlier
            // (HasUnstorableText runs before this check), so only a direct caller reaches this.
            return true;
        }

        return IsSecret(name, value);
    }

    private static List<ErrorDetail> CoverageDetails(
        JsonElement root,
        JsonElement planned,
        JsonElement matrix,
        out List<Guid> caseIds,
        out List<Guid> plannedCases)
    {
        var details = new List<ErrorDetail>();
        caseIds = [.. matrix.GetProperty("case_revision_ids").EnumerateArray().Select(id => id.GetGuid())];
        var variants = matrix.GetProperty("variants").EnumerateArray().Select(entry => VariantKey(entry.GetProperty("params"))).ToList();
        var envs = matrix.GetProperty("target_envs").EnumerateArray().Select(env => env.GetString()!).ToList();
        plannedCases = new List<Guid>(planned.GetArrayLength());

        var caseSet = new HashSet<Guid>();
        for (var i = 0; i < caseIds.Count; i++)
        {
            // The schema leaves uniqueness to this linear check (its pairwise uniqueItems would be quadratic).
            if (!caseSet.Add(caseIds[i]))
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/matrix/case_revision_ids/{i}"), null);
            }
        }

        var variantSet = variants.ToHashSet(StringComparer.Ordinal);
        var envSet = envs.ToHashSet(StringComparer.Ordinal);

        // Declared-empty is a matrix with no case at all; a matrix with cases but no variant or no environment is an empty
        // product nobody declared.
        if (caseIds.Count > 0 && variants.Count == 0)
        {
            Add(details, "/matrix/variants", null);
        }

        if (caseIds.Count > 0 && envs.Count == 0)
        {
            Add(details, "/matrix/target_envs", null);
        }

        var expected = (long)caseIds.Count * variants.Count * envs.Count;
        var count = planned.GetArrayLength();
        if (count != expected)
        {
            Add(details, "/planned_executions", null);
        }

        var seen = new HashSet<(Guid, string, string)>();
        var index = 0;
        foreach (var execution in planned.EnumerateArray())
        {
            var caseId = execution.GetProperty("case_revision_id").GetGuid();
            plannedCases.Add(caseId);
            var variant = VariantKey(execution.GetProperty("variant").GetProperty("params"));
            var env = execution.GetProperty("target_env").GetString()!;

            // A tuple outside the matrix, or the same tuple twice: either way the plan is not the matrix.
            if (!caseSet.Contains(caseId) || !variantSet.Contains(variant) || !envSet.Contains(env) || !seen.Add((caseId, variant, env)))
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/planned_executions/{index}"), index);
            }

            index++;
        }

        ShardDetails(root.GetProperty("shard_plan").GetProperty("shards"), count, details);

        var needIndex = 0;
        var needs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var need in root.GetProperty("env_needs").GetProperty("target_envs").EnumerateArray())
        {
            var env = need.GetProperty("target_env").GetString()!;
            if (!envSet.Contains(env) || !needs.Add(env))
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/env_needs/target_envs/{needIndex}"), null);
            }

            needIndex++;
        }

        return details;
    }

    /// <summary>
    /// The shards must partition <c>[0, count)</c>: sorted by start they begin at 0, each starts where the previous ended and
    /// the last ends at the planned count; a shard id is used once; an empty shard is allowed only in an empty plan.
    /// </summary>
    private static void ShardDetails(JsonElement shards, int count, List<ErrorDetail> details)
    {
        var ranges = shards.EnumerateArray()
            .Select((shard, position) => (
                Position: position,
                Id: shard.GetProperty("shard_id").GetString()!,
                From: shard.GetProperty("from_index").GetInt32(),
                To: shard.GetProperty("to_index").GetInt32()))
            .OrderBy(range => range.From)
            .ThenBy(range => range.To)
            .ToList();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var range in ranges)
        {
            var empty = range.From == range.To;
            if (range.From != next || range.To < range.From || (empty && count > 0) || !ids.Add(range.Id))
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/shard_plan/shards/{range.Position}"), null);
            }

            next = Math.Max(next, range.To);
        }

        if (next != count)
        {
            Add(details, "/shard_plan/shards", null);
        }
    }

    private static void Add(List<ErrorDetail> details, string path, int? index)
    {
        if (details.Count < MaxDetails)
        {
            details.Add(new ErrorDetail(path, MatrixNotCovered, index));
        }
    }

    /// <summary>An order-independent identity of a params object (the schema allows string values only).</summary>
    internal static string VariantKey(JsonElement parameters)
    {
        var pairs = parameters.EnumerateObject()
            .Select(member => (member.Name, Value: member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString()! : member.Value.GetRawText()))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal);
        var key = new StringBuilder();
        foreach (var (name, value) in pairs)
        {
            // Length-prefixed, so no character inside a name or a value can make two different sets read the same.
            key.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name)
                .Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        return key.ToString();
    }
}
