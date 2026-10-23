using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// The parsed, normalised mutable state of one authoring entity — the exact member set of the <c>content</c> snapshot
/// of 13.1.1 §4 line 207 and nothing else. The parser below is the one place that reads a caller's
/// <see cref="JsonElement"/> content, so the writer, the import and 13.1.2's apply all agree on the members.
/// </summary>
internal abstract record AuthoringSnapshot
{
    /// <summary>Which entity this snapshot describes.</summary>
    internal abstract AuthoringEntityKind EntityKind { get; }
}

/// <summary><c>feature_node</c> content: <c>{kind, title, position, parent_id}</c>.</summary>
internal sealed record FeatureNodeSnapshot(string Kind, string Title, int Position, Guid? ParentId) : AuthoringSnapshot
{
    internal override AuthoringEntityKind EntityKind => AuthoringEntityKind.FeatureNode;
}

/// <summary><c>checklist_item</c> content: <c>{feature_node_id, title, description, priority, state, position}</c>.</summary>
internal sealed record ChecklistItemSnapshot(
    Guid FeatureNodeId,
    string Title,
    string? Description,
    string Priority,
    string State,
    int Position) : AuthoringSnapshot
{
    internal override AuthoringEntityKind EntityKind => AuthoringEntityKind.ChecklistItem;
}

/// <summary>
/// <c>manual_test_case</c> content: <c>{checklist_item_id, code, title, description, precondition, steps, priority,
/// state}</c>. <paramref name="Steps"/> is the steps document as JSON text, already checked to be an object.
/// </summary>
internal sealed record ManualTestCaseSnapshot(
    Guid ChecklistItemId,
    string Code,
    string Title,
    string? Description,
    string? Precondition,
    string Steps,
    string Priority,
    string State) : AuthoringSnapshot
{
    internal override AuthoringEntityKind EntityKind => AuthoringEntityKind.ManualTestCase;
}

/// <summary>
/// The <c>content</c> snapshot of 13.1.1 §4 (AC5's <c>content_hash</c>, AC14's export-normalised projection): the
/// member sets are stated once here, the builders produce them in that order with NFC-normalised text, and
/// <see cref="Parse"/> refuses anything else. Excluded on purpose: <c>id</c>, <c>org_id</c>, <c>project_id</c>,
/// <c>created_at</c>, <c>updated_at</c>, <c>deleted_at</c>, <c>version</c> and the version row's <c>source</c>,
/// author and <c>reason</c>.
/// </summary>
internal static class AuthoringContent
{
    /// <summary>Longest accepted feature node title (§4: <c>char_length(title) between 1 and 200</c>).</summary>
    internal const int MaximumNodeTitleLength = 200;

    /// <summary>Longest accepted checklist item and manual case title (§4: 1..500).</summary>
    internal const int MaximumTitleLength = 500;

    /// <summary>Longest accepted description or precondition (§4: ≤ 4 000).</summary>
    internal const int MaximumLongTextLength = 4000;

    /// <summary>The exact, ordered member set of each entity's <c>content</c> object (§4 line 207).</summary>
    /// <param name="kind">The entity kind.</param>
    internal static IReadOnlyList<string> Members(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => ["kind", "title", "position", "parent_id"],
        AuthoringEntityKind.ChecklistItem => ["feature_node_id", "title", "description", "priority", "state", "position"],
        AuthoringEntityKind.ManualTestCase =>
            ["checklist_item_id", "code", "title", "description", "precondition", "steps", "priority", "state"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>
    /// Builds the <c>feature_node</c> content object. Every text member is NFC-normalised once, here, so the row and
    /// the snapshot can never disagree.
    /// </summary>
    internal static JsonObject FeatureNode(string kind, string title, int position, Guid? parentId) => new()
    {
        ["kind"] = kind,
        ["title"] = AuthoringText.Normalize(title),
        ["position"] = position,
        ["parent_id"] = parentId?.ToString("D"),
    };

    /// <summary>Builds the <c>checklist_item</c> content object.</summary>
    internal static JsonObject ChecklistItem(
        Guid featureNodeId,
        string title,
        string? description,
        string priority,
        string state,
        int position) => new()
        {
            ["feature_node_id"] = featureNodeId.ToString("D"),
            ["title"] = AuthoringText.Normalize(title),
            ["description"] = AuthoringText.Normalize(description),
            ["priority"] = priority,
            ["state"] = state,
            ["position"] = position,
        };

    /// <summary>Builds the <c>manual_test_case</c> content object.</summary>
    internal static JsonObject ManualTestCase(
        Guid checklistItemId,
        string code,
        string title,
        string? description,
        string? precondition,
        JsonNode? steps,
        string priority,
        string state) => new()
        {
            ["checklist_item_id"] = checklistItemId.ToString("D"),
            ["code"] = code,
            ["title"] = AuthoringText.Normalize(title),
            ["description"] = AuthoringText.Normalize(description),
            ["precondition"] = AuthoringText.Normalize(precondition),
            ["steps"] = steps?.DeepClone(),
            ["priority"] = priority,
            ["state"] = state,
        };

    /// <summary>The content object of a parsed snapshot: the writer's and the parser's one bridge.</summary>
    /// <param name="snapshot">The normalised snapshot.</param>
    internal static JsonObject Build(AuthoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot switch
        {
            FeatureNodeSnapshot node => FeatureNode(node.Kind, node.Title, node.Position, node.ParentId),
            ChecklistItemSnapshot item => ChecklistItem(item.FeatureNodeId, item.Title, item.Description, item.Priority, item.State, item.Position),
            ManualTestCaseSnapshot testCase => ManualTestCase(
                testCase.ChecklistItemId,
                testCase.Code,
                testCase.Title,
                testCase.Description,
                testCase.Precondition,
                JsonNode.Parse(testCase.Steps),
                testCase.Priority,
                testCase.State),
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.GetType(), "unknown authoring snapshot"),
        };
    }

    /// <summary>The canonical serialised form of a snapshot, which is what <c>content_hash</c> hashes.</summary>
    /// <param name="snapshot">The snapshot.</param>
    internal static string Serialize(AuthoringSnapshot snapshot) => Build(snapshot).ToJsonString();

    /// <summary>
    /// Reads and validates one caller-supplied <c>content</c> object: the exact member set of §4, NFC-normalised text,
    /// the plan's length limits, the closed enum sets, a <c>steps</c> object and a well-formed <c>code</c>. Every
    /// problem becomes an <c>ErrorDetail</c> whose path is relative to the content object, never a thrown exception.
    /// </summary>
    /// <param name="kind">The entity the content describes.</param>
    /// <param name="content">The caller's JSON content.</param>
    /// <param name="snapshot">The parsed snapshot on success.</param>
    /// <param name="details">The per-member refusals on failure.</param>
    internal static bool Parse(
        AuthoringEntityKind kind,
        JsonElement content,
        out AuthoringSnapshot? snapshot,
        out IReadOnlyList<(string Path, string Reason)> details)
    {
        snapshot = null;
        var problems = new List<(string Path, string Reason)>();

        if (content.ValueKind != JsonValueKind.Object)
        {
            details = [("/content", "object_required")];
            return false;
        }

        var members = Members(kind);
        foreach (var member in members)
        {
            if (!content.TryGetProperty(member, out _))
            {
                problems.Add(("/content/" + member, "required"));
            }
        }

        foreach (var property in content.EnumerateObject())
        {
            if (!members.Contains(property.Name, StringComparer.Ordinal))
            {
                problems.Add(("/content/" + property.Name, "unknown_member"));
            }
        }

        if (problems.Count > 0)
        {
            details = problems;
            return false;
        }

        snapshot = kind switch
        {
            AuthoringEntityKind.FeatureNode => ParseNode(content, problems),
            AuthoringEntityKind.ChecklistItem => ParseItem(content, problems),
            AuthoringEntityKind.ManualTestCase => ParseCase(content, problems),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
        };

        if (problems.Count > 0)
        {
            snapshot = null;
            details = problems;
            return false;
        }

        details = [];
        return true;
    }

    private static FeatureNodeSnapshot? ParseNode(JsonElement content, List<(string Path, string Reason)> problems)
    {
        var kind = Text(content, "kind", "kind", problems);
        if (kind is not null && !FeatureNodeKinds.IsValid(kind))
        {
            problems.Add(("/content/kind", "value_invalid"));
        }

        var title = Text(content, "title", "title", problems);
        Length(title, "title", MaximumNodeTitleLength, problems);
        var position = Integer(content, "position", "position", problems);
        var parentId = Uuid(content, "parent_id", "parent_id", problems, required: false);

        return kind is null || title is null || position is null
            ? null
            : new FeatureNodeSnapshot(kind, title, position.Value, parentId);
    }

    private static ChecklistItemSnapshot? ParseItem(JsonElement content, List<(string Path, string Reason)> problems)
    {
        var nodeId = Uuid(content, "feature_node_id", "feature_node_id", problems, required: true);
        var title = Text(content, "title", "title", problems);
        Length(title, "title", MaximumTitleLength, problems);
        var description = NullableText(content, "description", "description", problems);
        var priority = Text(content, "priority", "priority", problems);
        if (priority is not null && !AuthoringPriorities.IsValid(priority))
        {
            problems.Add(("/content/priority", "value_invalid"));
        }

        var state = Text(content, "state", "state", problems);
        if (state is not null && !ChecklistItemStateMachine.States.Contains(state, StringComparer.Ordinal))
        {
            problems.Add(("/content/state", "value_invalid"));
        }

        var position = Integer(content, "position", "position", problems);

        return nodeId is null || title is null || priority is null || state is null || position is null
            ? null
            : new ChecklistItemSnapshot(nodeId.Value, title, description, priority, state, position.Value);
    }

    private static ManualTestCaseSnapshot? ParseCase(JsonElement content, List<(string Path, string Reason)> problems)
    {
        var itemId = Uuid(content, "checklist_item_id", "checklist_item_id", problems, required: true);
        var code = Text(content, "code", "code", problems);
        if (code is not null && !AuthoringCode.TryParse(code, out _))
        {
            problems.Add(("/content/code", "value_invalid"));
        }

        var title = Text(content, "title", "title", problems);
        Length(title, "title", MaximumTitleLength, problems);
        var description = NullableText(content, "description", "description", problems);
        var precondition = NullableText(content, "precondition", "precondition", problems);
        var steps = Steps(content, problems);
        var priority = Text(content, "priority", "priority", problems);
        if (priority is not null && !AuthoringPriorities.IsValid(priority))
        {
            problems.Add(("/content/priority", "value_invalid"));
        }

        var state = Text(content, "state", "state", problems);
        if (state is not null && !ManualTestCaseStateMachine.States.Contains(state, StringComparer.Ordinal))
        {
            problems.Add(("/content/state", "value_invalid"));
        }

        return itemId is null || code is null || title is null || steps is null || priority is null || state is null
            ? null
            : new ManualTestCaseSnapshot(itemId.Value, code, title, description, precondition, steps, priority, state);
    }

    private static string? Text(JsonElement content, string member, string path, List<(string Path, string Reason)> problems)
    {
        if (!content.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String)
        {
            problems.Add(("/content/" + path, "string_required"));
            return null;
        }

        return AuthoringText.Normalize(value.GetString());
    }

    private static string? NullableText(JsonElement content, string member, string path, List<(string Path, string Reason)> problems)
    {
        if (!content.TryGetProperty(member, out var value))
        {
            problems.Add(("/content/" + path, "required"));
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                var text = AuthoringText.Normalize(value.GetString());
                Length(text, path, MaximumLongTextLength, problems);
                return text;
            default:
                problems.Add(("/content/" + path, "string_or_null_required"));
                return null;
        }
    }

    private static int? Integer(JsonElement content, string member, string path, List<(string Path, string Reason)> problems)
    {
        if (!content.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            problems.Add(("/content/" + path, "integer_required"));
            return null;
        }

        if (!FeatureNodeRules.IsValidPosition(number))
        {
            problems.Add(("/content/" + path, "value_invalid"));
            return null;
        }

        return number;
    }

    private static Guid? Uuid(JsonElement content, string member, string path, List<(string Path, string Reason)> problems, bool required)
    {
        if (!content.TryGetProperty(member, out var value))
        {
            problems.Add(("/content/" + path, "required"));
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                problems.Add(("/content/" + path, "required"));
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(value.GetString(), "D", out var id))
        {
            problems.Add(("/content/" + path, "uuid_required"));
            return null;
        }

        return id;
    }

    private static string? Steps(JsonElement content, List<(string Path, string Reason)> problems)
    {
        if (!content.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Object)
        {
            problems.Add(("/content/steps", "object_required"));
            return null;
        }

        // Every string leaf is NFC-normalised before the document is stored, so the composed and the decomposed spelling
        // of one step are one stored document, one content_hash and one version (AC14). Rebuilding the node tree keeps
        // member order, so the serialised text is stable.
        return JsonNode.Parse(steps.GetRawText()) is { } node
            ? NormaliseLeaves(node).ToJsonString()
            : steps.GetRawText();
    }

    /// <summary>NFC-normalises every string leaf of a JSON document in place; non-string leaves are left alone.</summary>
    /// <param name="node">The document or subtree.</param>
    /// <returns>The same node, normalised.</returns>
    private static JsonNode NormaliseLeaves(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToList())
                {
                    if (property.Value is { } child)
                    {
                        jsonObject[property.Key] = NormaliseLeaves(child);
                    }
                }

                return jsonObject;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is { } child)
                    {
                        jsonArray[index] = NormaliseLeaves(child);
                    }
                }

                return jsonArray;

            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(AuthoringText.Normalize(text)!);

            default:
                return node;
        }
    }

    private static void Length(string? text, string path, int maximum, List<(string Path, string Reason)> problems)
    {
        if (text is null)
        {
            return;
        }

        if (text.Length == 0)
        {
            problems.Add(("/content/" + path, "required"));
        }
        else if (!AuthoringText.Fits(text, maximum))
        {
            problems.Add(("/content/" + path, "too_long"));
        }
    }
}
