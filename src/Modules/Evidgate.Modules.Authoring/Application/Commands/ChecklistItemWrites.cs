using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// The request-shape rules of the two checklist item writes (13.1.1 §5 routes 8 and 9, AC3 and AC20): the bounds the
/// <c>checklist_item</c> CHECK constraints of <c>migrations/0039_authoring_tree.sql:270-275</c> enforce, answered at the
/// edge with the member path the client sent (<c>/title</c>, <c>/priority</c>, …) instead of the writer's internal
/// <c>/changes/0/content/…</c> path.
/// </summary>
/// <remarks>
/// The parent rule (AC3's <c>category</c>/<c>sub_category</c> restriction) is deliberately **not** repeated here: the
/// version writer owns placement (<c>CheckItemParentAsync</c>) and answers
/// <c>422 authoring_parent_invalid</c> with <c>parent_not_found</c>, <c>parent_kind_invalid</c> or <c>parent_deleted</c>
/// after its own locked read, so a second copy could only drift from it. Every rule below is a bound on a value the
/// caller sends, which the writer would otherwise report against its own batch path.
/// </remarks>
internal static class ChecklistItemFields
{
    /// <summary>The <c>checklist_item_title_check</c> bound (0039:270).</summary>
    internal const int MaxTitleLength = 500;

    /// <summary>The <c>checklist_item_description_check</c> bound (0039:271).</summary>
    internal const int MaxDescriptionLength = 4_000;

    /// <summary>The AC20 reason bound, shared with the writer's <c>ReasonRequired</c> rule.</summary>
    internal const int MaxReasonLength = 500;

    /// <summary>The members <c>POST …/checklist-items</c> accepts (13.1.1 §5 route 8).</summary>
    internal static readonly string[] CreateMembers = ["node_id", "title", "description", "priority", "position"];

    /// <summary>
    /// The members <c>PATCH …/checklist-items/{i}</c> accepts (13.1.1 §5 route 9). <c>node_id</c> is absent on purpose:
    /// an item does not move between nodes on this route, so the endpoint refuses it as an unknown member.
    /// </summary>
    internal static readonly string[] UpdateMembers = ["title", "description", "priority", "position", "state", "reason"];

    /// <summary>
    /// The writer's <c>content</c> member names as these routes spell them: the column is <c>feature_node_id</c> while
    /// the body member of route 8 is <c>node_id</c>, so a placement refusal reaches the client against the member it sent
    /// (<see cref="AuthoringErrors.RequestFacing"/>).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RequestMemberNames =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["feature_node_id"] = "node_id" };

    /// <summary>Reads and validates the create body, naming every offending member at once.</summary>
    /// <param name="body">The parsed, member-checked request body.</param>
    /// <returns>The draft, or the 422 carrying one detail per problem.</returns>
    internal static (ChecklistItemDraft? Draft, Error? Error) ReadCreate(JsonElement body)
    {
        var problems = new List<ErrorDetail>();

        var nodeId = ReadUuid(body, "node_id", problems);
        var title = ReadText(body, "title", required: true, MaxTitleLength, problems);
        var description = ReadOptionalText(body, "description", MaxDescriptionLength, problems);
        var priority = ReadPriority(body, required: true, problems);
        var position = ReadPosition(body, problems);

        if (problems.Count > 0)
        {
            return (null, AuthoringErrors.InvalidMany("the checklist item create body is not valid", problems));
        }

        return (new ChecklistItemDraft(nodeId!.Value, title!, description, priority!, position), null);
    }

    /// <summary>
    /// Reads the members of a PATCH body, keeping "absent" apart from "explicitly null": only <c>description</c> can be
    /// cleared, and every other absent member is left unchanged (13.1.1 §5 route 9).
    /// </summary>
    /// <param name="body">The parsed, member-checked request body.</param>
    /// <returns>The edit, or the 422 carrying one detail per problem.</returns>
    internal static (ChecklistItemEdit? Edit, Error? Error) ReadUpdate(JsonElement body)
    {
        var problems = new List<ErrorDetail>();

        var hasTitle = body.TryGetProperty("title", out _);
        var title = hasTitle ? ReadText(body, "title", required: true, MaxTitleLength, problems) : null;

        var hasDescription = body.TryGetProperty("description", out _);
        var description = hasDescription ? ReadOptionalText(body, "description", MaxDescriptionLength, problems) : null;

        var hasPriority = body.TryGetProperty("priority", out _);
        var priority = hasPriority ? ReadPriority(body, required: true, problems) : null;

        var hasPosition = body.TryGetProperty("position", out _);
        var position = hasPosition ? ReadPosition(body, problems) : null;

        var hasState = body.TryGetProperty("state", out _);
        var state = hasState ? ReadState(body, problems) : null;

        var hasReason = body.TryGetProperty("reason", out _);
        var reason = hasReason ? ReadText(body, "reason", required: true, MaxReasonLength, problems) : null;

        if (problems.Count > 0)
        {
            return (null, AuthoringErrors.InvalidMany("the checklist item update body is not valid", problems));
        }

        return (
            new ChecklistItemEdit(
                hasTitle,
                title,
                hasDescription,
                description,
                hasPriority,
                priority,
                hasPosition,
                position,
                hasState,
                state,
                hasReason,
                reason),
            null);
    }

    /// <summary>
    /// The optional <c>{reason?}</c> of a delete (13.1.1 §5 route 10): absent is fine, a present one is 1..500
    /// characters.
    /// </summary>
    /// <param name="body">The optional body; <see cref="JsonValueKind.Undefined"/> for a bodiless delete.</param>
    /// <returns>The reason, or the 422.</returns>
    internal static (string? Reason, Error? Error) ReadDeleteReason(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("reason", out _))
        {
            return (null, null);
        }

        var problems = new List<ErrorDetail>();
        var reason = ReadText(body, "reason", required: true, MaxReasonLength, problems);

        return problems.Count > 0
            ? (null, AuthoringErrors.InvalidMany("the delete reason is not valid", problems))
            : (reason, null);
    }

    /// <summary>
    /// AC20's reason rule, decided at the edge so the answer names <c>/reason</c>: a move **into** <c>retired</c>
    /// requires a 1..500 character reason, and any other move must not carry one.
    /// </summary>
    /// <param name="currentState">The item's stored state.</param>
    /// <param name="targetState">The state the PATCH asks for.</param>
    /// <param name="reason">The reason the PATCH carries, or null.</param>
    /// <returns>The 422, or null when the pair is answerable.</returns>
    internal static Error? CheckReason(string currentState, string targetState, string? reason)
    {
        if (targetState == ChecklistItemStateMachine.Retired && reason is null)
        {
            return AuthoringErrors.Invalid(
                "/reason",
                "required",
                "retiring a checklist item needs a reason of 1..500 characters");
        }

        if (targetState != ChecklistItemStateMachine.Retired
            && currentState == ChecklistItemStateMachine.Retired
            && reason is not null)
        {
            return AuthoringErrors.Invalid("/reason", "invalid", "a reason belongs to the move into retired");
        }

        return null;
    }

    /// <summary>The stored snapshot as the writer's content document (<c>AuthoringContent.ParseItem</c>'s members).</summary>
    /// <param name="item">The snapshot, already carrying the edited members.</param>
    /// <returns>The <c>content</c> the version row stores.</returns>
    internal static JsonElement Content(ChecklistItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return Content(item.NodeId, item.Title, item.Description, item.Priority, item.State, item.Position);
    }

    /// <summary>The stored snapshot as the writer's content document, for a row that does not exist yet.</summary>
    /// <param name="nodeId">The parent node.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">The optional description.</param>
    /// <param name="priority">The priority.</param>
    /// <param name="state">The state.</param>
    /// <param name="position">The position.</param>
    /// <returns>The <c>content</c> the version row stores.</returns>
    internal static JsonElement Content(
        Guid nodeId,
        string title,
        string? description,
        string priority,
        string state,
        int position) => JsonSerializer.SerializeToElement(
            new ChecklistItemContent(nodeId, title, description, priority, state, position));

    private static string? ReadText(
        JsonElement body,
        string member,
        bool required,
        int maxLength,
        List<ErrorDetail> problems)
    {
        if (!body.TryGetProperty(member, out var value))
        {
            problems.Add(new ErrorDetail("/" + member, "required"));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            problems.Add(new ErrorDetail("/" + member, required ? "string_required" : "string_or_null_required"));
            return null;
        }

        var text = value.GetString()!;

        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add(new ErrorDetail("/" + member, "required"));
            return null;
        }

        if (text.Length > maxLength)
        {
            problems.Add(new ErrorDetail("/" + member, "too_long"));
            return null;
        }

        return text;
    }

    private static string? ReadOptionalText(JsonElement body, string member, int maxLength, List<ErrorDetail> problems)
    {
        if (!body.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            problems.Add(new ErrorDetail("/" + member, "string_or_null_required"));
            return null;
        }

        var text = value.GetString()!;

        if (text.Length > maxLength)
        {
            problems.Add(new ErrorDetail("/" + member, "too_long"));
            return null;
        }

        return text;
    }

    private static string? ReadPriority(JsonElement body, bool required, List<ErrorDetail> problems)
    {
        if (!body.TryGetProperty("priority", out var value))
        {
            problems.Add(new ErrorDetail("/priority", "required"));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            problems.Add(new ErrorDetail("/priority", required ? "string_required" : "string_or_null_required"));
            return null;
        }

        var priority = value.GetString()!;

        if (!AuthoringPriorities.IsValid(priority))
        {
            problems.Add(new ErrorDetail("/priority", "value_invalid"));
            return null;
        }

        return priority;
    }

    private static string? ReadState(JsonElement body, List<ErrorDetail> problems)
    {
        if (!body.TryGetProperty("state", out var value) || value.ValueKind != JsonValueKind.String)
        {
            problems.Add(new ErrorDetail("/state", "string_required"));
            return null;
        }

        var state = value.GetString()!;

        if (!ChecklistItemStateMachine.States.Contains(state, StringComparer.Ordinal))
        {
            problems.Add(new ErrorDetail("/state", "value_invalid"));
            return null;
        }

        return state;
    }

    private static int? ReadPosition(JsonElement body, List<ErrorDetail> problems)
    {
        if (!body.TryGetProperty("position", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var position))
        {
            problems.Add(new ErrorDetail("/position", "integer_required"));
            return null;
        }

        if (!FeatureNodeRules.IsValidPosition(position))
        {
            problems.Add(new ErrorDetail("/position", "value_invalid"));
            return null;
        }

        return position;
    }

    private static Guid? ReadUuid(JsonElement body, string member, List<ErrorDetail> problems)
    {
        if (!body.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String)
        {
            problems.Add(new ErrorDetail("/" + member, "required"));
            return null;
        }

        if (!Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty)
        {
            problems.Add(new ErrorDetail("/" + member, "invalid"));
            return null;
        }

        return id;
    }

    /// <summary>The create body after validation (13.1.1 §5 route 8).</summary>
    /// <param name="NodeId">The live category/sub-category node.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Description">The optional description.</param>
    /// <param name="Priority">The validated priority.</param>
    /// <param name="Position">The optional position.</param>
    internal sealed record ChecklistItemDraft(Guid NodeId, string Title, string? Description, string Priority, int? Position);

    /// <summary>One PATCH body with presence flags, so an absent member is left unchanged (13.1.1 §5 route 9).</summary>
    internal readonly record struct ChecklistItemEdit(
        bool HasTitle,
        string? Title,
        bool HasDescription,
        string? Description,
        bool HasPriority,
        string? Priority,
        bool HasPosition,
        int? Position,
        bool HasState,
        string? State,
        bool HasReason,
        string? Reason);

    /// <summary>The writer's <c>content</c> document of one checklist item, in the members its parser reads.</summary>
    private sealed record ChecklistItemContent(
        [property: JsonPropertyName("feature_node_id")] Guid FeatureNodeId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("position")] int Position);
}

/// <summary>
/// The item row an edit or a delete takes before it writes, in the writer's own lock order (node, then item) so a
/// concurrent batch that touches a node and one of its items can never deadlock against this command.
/// </summary>
internal static class ChecklistItemLock
{
    /// <summary>
    /// Reads the item, locks its parent node <c>for share</c> and then the item <c>for update</c>, answering the locked
    /// row so the version check of AC19 is atomic with the write that follows it.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project the route addresses.</param>
    /// <param name="itemId">The item from the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The locked row, or the 404/503 to return unchanged.</returns>
    internal static async ValueTask<(ChecklistItemDto? Item, Error? Error)> LockAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var peek = await db.QuerySingleOrDefaultAsync<ChecklistItemDto>(
            AuthoringChecklistReadSql.One,
            new { org_id = orgId, project_id = projectId, item_id = itemId },
            cancellationToken).ConfigureAwait(false);

        if (peek is null)
        {
            return (null, AuthoringErrors.EntityNotFound(AuthoringEntityKind.ChecklistItem));
        }

        try
        {
            await db.QuerySingleOrDefaultAsync<int>(
                AuthoringChecklistReadSql.LockParentShare,
                new { org_id = orgId, project_id = projectId, node_id = peek.NodeId },
                cancellationToken).ConfigureAwait(false);

            var locked = await db.QuerySingleOrDefaultAsync<ChecklistItemDto>(
                AuthoringChecklistReadSql.LockItem,
                new { org_id = orgId, project_id = projectId, item_id = itemId },
                cancellationToken).ConfigureAwait(false);

            return locked is null
                ? (null, AuthoringErrors.EntityNotFound(AuthoringEntityKind.ChecklistItem))
                : (locked, null);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable)
        {
            // The writer maps the same two SQLSTATEs of its own statements; a lock this command takes first must answer
            // the same retryable 503 rather than surface as a 500 (13.1.1 §5, 00 §9.2).
            return (null, AuthoringErrors.Unavailable());
        }
    }
}
