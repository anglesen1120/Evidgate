using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Domain.Changes;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// A validated queue payload: the raw document the table stores unchanged, the server-derived affected set the queue
/// handler stamps into <c>affected_versions</c>, and the split's case assignments the handler checks against the target.
/// Nothing here is taken from a client-supplied version map.
/// </summary>
/// <param name="ChangeType">One of <see cref="AuthoringChangeTypes"/>.</param>
/// <param name="AffectedItemIds">The target for comment/delete/split, the merge's sources (survivor included).</param>
/// <param name="CaseAssignments">
/// The split's <c>case_assignments</c> entries with their payload indexes, in payload order; empty for the other types.
/// </param>
/// <param name="RawJson">The payload as the caller sent it, already size-capped.</param>
internal sealed record AuthoringChangePayload(
    string ChangeType,
    IReadOnlyList<Guid> AffectedItemIds,
    IReadOnlyList<(int Index, Guid CaseId)> CaseAssignments,
    string RawJson)
{
    /// <summary>True for delete/merge/split, the types the pending-overlap rule covers.</summary>
    internal bool IsStructural => AuthoringChangeTypes.IsStructural(ChangeType);
}

/// <summary>
/// The semantic half of the queue body's validation: the cross-field rules a JSON Schema cannot express — the
/// survivor inside the source list, the target equal to it, unique ids, a case assignment inside its own new items —
/// and the affected-set derivation of the story's shared contract.
/// </summary>
/// <remarks>
/// The wire shape (closed objects, uuid format, array bounds) is the contract filter's job at the edge; this reader
/// re-checks the same bounds because a handler must fail closed when it is reached without that stage, and it adds
/// what the schema cannot say. Each refusal names the exact member so the UI can point at it, and the reasons are
/// the sixteen <c>authoring.reason.*</c> keys the web catalogue ships.
/// </remarks>
internal static class AuthoringChangePayloadReader
{
    /// <summary>Validates one queue payload and derives its affected set, or answers the refusal to return unchanged.</summary>
    /// <param name="changeType">The request's <c>change_type</c>.</param>
    /// <param name="targetId">The request's <c>target_id</c>.</param>
    /// <param name="payload">The request's parsed <c>payload</c>.</param>
    internal static (AuthoringChangePayload? Payload, Error? Refusal) Read(
        string? changeType,
        Guid targetId,
        JsonElement payload)
    {
        if (!AuthoringChangeTypes.IsValid(changeType))
        {
            return (null, AuthoringChangeErrors.PayloadInvalid("/change_type"));
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return (null, AuthoringChangeErrors.PayloadInvalid("/payload"));
        }

        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > AuthoringChangeLimits.PayloadMaxBytes)
        {
            return (null, AuthoringChangeErrors.PayloadInvalid("/payload"));
        }

        var assignments = new List<(int Index, Guid CaseId)>();

        var refusal = changeType switch
        {
            AuthoringChangeTypes.Comment => ReadComment(payload),
            AuthoringChangeTypes.Delete => NoMembers(payload),
            AuthoringChangeTypes.Merge => ReadMerge(payload, targetId),
            _ => ReadSplit(payload, assignments),
        };

        if (refusal is not null)
        {
            return (null, refusal);
        }

        var affected = changeType == AuthoringChangeTypes.Merge
            ? SourceItemIds(payload)
            : [targetId];

        return (new AuthoringChangePayload(changeType!, affected, assignments, payload.GetRawText()), null);
    }

    /// <summary>
    /// A comment is a closed object with one body of 1..2 000 characters. A missing, mistyped, empty or over-long body is
    /// the <c>comment_body_invalid</c> reason the review panel renders; any other shape defect stays <c>payload_invalid</c>.
    /// </summary>
    private static Error? ReadComment(JsonElement payload)
    {
        if (UnknownMember(payload, "/payload", "body") is { } unknown)
        {
            return unknown;
        }

        if (!payload.TryGetProperty("body", out var body)
            || body.ValueKind != JsonValueKind.String
            || body.GetString() is not { } text
            || !HasLength(text, AuthoringChangeLimits.CommentBodyMin, AuthoringChangeLimits.CommentBodyMax))
        {
            return AuthoringErrors.Invalid(
                "/payload/body", "comment_body_invalid", "a comment body must be 1..2000 characters");
        }

        return null;
    }

    /// <summary>A delete carries no members: the target is the change's own address.</summary>
    private static Error? NoMembers(JsonElement payload) =>
        payload.EnumerateObject().Any() ? AuthoringChangeErrors.PayloadInvalid("/payload") : null;

    /// <summary>
    /// A merge names 2..50 unique live items including the survivor, and its <c>target_id</c> is that survivor.
    /// The count refusals use their own reasons so the UI can say "at least 2" and "at most 50" instead of a
    /// generic invalid payload.
    /// </summary>
    private static Error? ReadMerge(JsonElement payload, Guid targetId)
    {
        if (UnknownMember(payload, "/payload", "source_item_ids", "survivor_item_id") is { } unknown)
        {
            return unknown;
        }

        if (!payload.TryGetProperty("survivor_item_id", out var survivorElement)
            || !TryReadId(survivorElement, out var survivor))
        {
            return AuthoringChangeErrors.PayloadInvalid("/payload/survivor_item_id");
        }

        if (!payload.TryGetProperty("source_item_ids", out var sourcesElement)
            || sourcesElement.ValueKind != JsonValueKind.Array)
        {
            return AuthoringChangeErrors.PayloadInvalid("/payload/source_item_ids");
        }

        var count = sourcesElement.GetArrayLength();
        if (count < AuthoringChangeLimits.MergeSourcesMin)
        {
            return AuthoringErrors.Invalid(
                "/payload/source_item_ids", "merge_sources_min", "at least 2 items are needed to merge");
        }

        if (count > AuthoringChangeLimits.MergeSourcesMax)
        {
            return AuthoringErrors.Invalid(
                "/payload/source_item_ids", "merge_sources_max", "at most 50 items can be merged at once");
        }

        var sources = new HashSet<Guid>();
        var index = 0;
        foreach (var element in sourcesElement.EnumerateArray())
        {
            if (!TryReadId(element, out var id) || !sources.Add(id))
            {
                return AuthoringChangeErrors.PayloadInvalid(
                    string.Create(CultureInfo.InvariantCulture, $"/payload/source_item_ids/{index}"));
            }

            index++;
        }

        if (!sources.Contains(survivor))
        {
            return AuthoringErrors.Invalid(
                "/payload/survivor_item_id", "survivor_not_in_sources", "the surviving item must be one of the merged items");
        }

        return targetId == survivor ? null : AuthoringChangeErrors.PayloadInvalid("/target_id");
    }

    /// <summary>
    /// A split names 2..20 new items and, optionally, a 0-based new-item index per existing case. The index is
    /// checked against the array it indexes, which the schema's 0..19 bound alone cannot do.
    /// </summary>
    private static Error? ReadSplit(
        JsonElement payload,
        List<(int Index, Guid CaseId)> assignmentsInto)
    {
        if (UnknownMember(payload, "/payload", "new_items", "case_assignments") is { } unknown)
        {
            return unknown;
        }

        if (!payload.TryGetProperty("new_items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return AuthoringChangeErrors.PayloadInvalid("/payload/new_items");
        }

        var count = itemsElement.GetArrayLength();
        if (count < AuthoringChangeLimits.SplitItemsMin)
        {
            return AuthoringErrors.Invalid("/payload/new_items", "split_items_min", "2-20 items are needed to split");
        }

        if (count > AuthoringChangeLimits.SplitItemsMax)
        {
            return AuthoringErrors.Invalid("/payload/new_items", "split_items_max", "2-20 items are needed to split");
        }

        var index = 0;
        foreach (var item in itemsElement.EnumerateArray())
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"/payload/new_items/{index}");
            if (ReadSplitItem(item, path) is { } refusal)
            {
                return refusal;
            }

            index++;
        }

        return payload.TryGetProperty("case_assignments", out var assignments)
            ? ReadCaseAssignments(assignments, count, assignmentsInto)
            : null;
    }

    /// <summary>One new item: a 1..500 character title, an optional description and a closed priority.</summary>
    private static Error? ReadSplitItem(JsonElement item, string path)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return AuthoringChangeErrors.PayloadInvalid(path);
        }

        if (UnknownMember(item, path, "title", "description", "priority") is { } unknown)
        {
            return unknown;
        }

        if (!item.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String
            || title.GetString() is not { } text
            || !HasLength(text, 1, AuthoringChangeLimits.ItemTitleMax)
            || string.IsNullOrWhiteSpace(text))
        {
            return AuthoringChangeErrors.PayloadInvalid(path + "/title");
        }

        if (item.TryGetProperty("description", out var description)
            && description.ValueKind is not JsonValueKind.Null
            && (description.ValueKind != JsonValueKind.String
                || !RuneCount(description.GetString()!)
                    .IsBetween(0, AuthoringChangeLimits.ItemDescriptionMax)))
        {
            return AuthoringChangeErrors.PayloadInvalid(path + "/description");
        }

        if (item.TryGetProperty("priority", out var priority)
            && (priority.ValueKind != JsonValueKind.String || !AuthoringPriorities.IsValid(priority.GetString())))
        {
            return AuthoringChangeErrors.PayloadInvalid(path + "/priority");
        }

        return null;
    }

    /// <summary>
    /// The optional case assignments: at most 200, each naming a distinct case and one of the new items by 0-based index.
    /// Whether that case is a live child of the split target needs a tenant read, so the queue handler answers it; the
    /// bound here is what a 200-entry array means before the payload's own jsonb size fence can refuse it.
    /// </summary>
    private static Error? ReadCaseAssignments(
        JsonElement assignments,
        int newItemCount,
        List<(int Index, Guid CaseId)> into)
    {
        if (assignments.ValueKind != JsonValueKind.Array
            || assignments.GetArrayLength() > AuthoringChangeLimits.CaseAssignmentsMax)
        {
            return AuthoringChangeErrors.PayloadInvalid("/payload/case_assignments");
        }

        var seen = new HashSet<Guid>();
        var index = 0;
        foreach (var assignment in assignments.EnumerateArray())
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"/payload/case_assignments/{index}");

            if (assignment.ValueKind != JsonValueKind.Object)
            {
                return AuthoringChangeErrors.PayloadInvalid(path);
            }

            if (UnknownMember(assignment, path, "case_id", "item_index") is { } unknown)
            {
                return unknown;
            }

            if (!assignment.TryGetProperty("case_id", out var caseElement)
                || !TryReadId(caseElement, out var caseId)
                || !seen.Add(caseId))
            {
                return AuthoringChangeErrors.PayloadInvalid(path + "/case_id");
            }

            if (!assignment.TryGetProperty("item_index", out var itemIndex)
                || itemIndex.ValueKind != JsonValueKind.Number
                || !itemIndex.TryGetInt32(out var position)
                || position < 0
                || position >= newItemCount)
            {
                return AuthoringChangeErrors.PayloadInvalid(path + "/item_index");
            }

            into.Add((index, caseId));
            index++;
        }

        return null;
    }

    /// <summary>The first property the payload does not allow, as its own 422, or null when all are known.</summary>
    /// <param name="payload">The object whose members are closed.</param>
    /// <param name="pointer">The JSON pointer of <paramref name="payload"/> itself.</param>
    /// <param name="allowed">The member names the change type accepts.</param>
    private static Error? UnknownMember(JsonElement payload, string pointer, params string[] allowed)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                return AuthoringChangeErrors.PayloadInvalid(pointer + "/" + property.Name);
            }
        }

        return null;
    }

    /// <summary>The merge's source ids, in payload order, already validated as unique uuids.</summary>
    private static List<Guid> SourceItemIds(JsonElement payload)
    {
        var ids = new List<Guid>();

        foreach (var element in payload.GetProperty("source_item_ids").EnumerateArray())
        {
            if (TryReadId(element, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>A uuid member: a string in canonical or any "D"-parsable form, and never the empty uuid.</summary>
    private static bool TryReadId(JsonElement element, out Guid id)
    {
        id = Guid.Empty;

        return element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out id)
            && id != Guid.Empty;
    }

    /// <summary>The number of Unicode code points, which is what PostgreSQL's <c>char_length</c> counts.</summary>
    private static int RuneCount(string text) => text.EnumerateRunes().Count();

    /// <summary>True when <paramref name="text"/> is inside the inclusive code-point range.</summary>
    private static bool HasLength(string text, int min, int max) => RuneCount(text).IsBetween(min, max);
}

/// <summary>The two inclusive-bound checks this reader needs, kept together so the intent stays readable.</summary>
internal static class AuthoringChangeRanges
{
    /// <summary>True when <paramref name="value"/> is within the inclusive bounds.</summary>
    internal static bool IsBetween(this int value, int min, int max) => value >= min && value <= max;
}

/// <summary>
/// The queue body's whole-document check: <c>contracts/schemas/authoring-change.v1.json</c> through the compiled contract
/// set, so every root member the route accepts is one the schema declares.
/// </summary>
/// <remarks>
/// It runs after <see cref="AuthoringChangePayloadReader"/> so the reader's own reasons stay the ones a caller sees — a
/// merge of 51 sources is <c>merge_sources_max</c>, not the schema's <c>max_items</c>. What is left for this check is
/// exactly what a projection cannot report: an absent <c>payload</c>, an unknown root member, a mistyped or over-long
/// <c>reason</c>.
/// </remarks>
internal static class AuthoringChangeDocument
{
    /// <summary>The key of <c>contracts/schemas/authoring-change.v1.json</c> in the compiled set.</summary>
    internal const string SchemaKey = "authoring-change.v1";

    /// <summary>Validates one whole queue body, or answers the 422 the schema's own detail rows produce.</summary>
    /// <param name="body">The request body as the edge parsed it.</param>
    /// <param name="schemas">The compiled contract set of the host.</param>
    internal static Error? Validate(JsonElement body, ContractSchemaSet schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        var verdict = schemas.Validate(SchemaKey, body);

        return verdict.IsValid
            ? null
            : AuthoringErrors.InvalidMany(
                "the request body does not match authoring-change.v1",
                [.. verdict.Errors.Select(error => new ErrorDetail(error.Path, error.Reason, error.Index))]);
    }
}
