using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Mediator;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>The body readers of the case routes: one precise 422 per member instead of a single opaque refusal.</summary>
internal static class ManualCaseBody
{
    /// <summary>The members <c>POST …/manual-cases</c> accepts.</summary>
    internal static readonly string[] CreateMembers =
        ["checklist_item_id", "title", "description", "precondition", "steps", "priority"];

    /// <summary>
    /// The members <c>PATCH …/manual-cases/{m}</c> accepts — <c>code</c> and <c>checklist_item_id</c> are listed so the
    /// handler answers them with their own 422 paths (AC22/AC4) rather than as an unknown field.
    /// </summary>
    internal static readonly string[] UpdateMembers =
        ["title", "description", "precondition", "steps", "priority", "code", "checklist_item_id"];

    /// <summary>The members <c>POST …/manual-cases/{m}/state</c> accepts.</summary>
    internal static readonly string[] StateMembers = ["state", "reason"];

    /// <summary>The members <c>POST …/manual-cases/{m}/item</c> accepts.</summary>
    internal static readonly string[] MoveMembers = ["checklist_item_id", "reason"];

    /// <summary>The members <c>POST …/manual-cases/{m}/links</c> accepts.</summary>
    internal static readonly string[] LinkMembers = ["test_case_id"];

    /// <summary>A required uuid member.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="member">The member name.</param>
    /// <param name="id">The id on success.</param>
    internal static Error? RequiredId(JsonElement body, string member, out Guid id)
    {
        id = Guid.Empty;

        if (!body.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid($"{member} is required", [new ErrorDetail("/" + member, "required")]);
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out id) || id == Guid.Empty)
        {
            return Error.Invalid($"{member} must be a uuid", [new ErrorDetail("/" + member, "value_invalid")]);
        }

        return null;
    }

    /// <summary>A required 1..<paramref name="max"/> code-point title.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="title">The title on success.</param>
    internal static Error? RequiredTitle(JsonElement body, out string title)
    {
        title = string.Empty;

        if (!body.TryGetProperty("title", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid("title is required", [new ErrorDetail("/title", "required")]);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("title must be a string", [new ErrorDetail("/title", "invalid_type")]);
        }

        title = value.GetString()!;

        if (AuthoringText.CodePointLength(title) == 0)
        {
            return Error.Invalid("title is required", [new ErrorDetail("/title", "required")]);
        }

        return AuthoringText.Fits(title, AuthoringContent.MaximumTitleLength)
            ? null
            : Error.Invalid("title is too long", [new ErrorDetail("/title", "too_long")]);
    }

    /// <summary>An optional title: absent is unchanged, an explicit null or an empty string is refused.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="title">The title when it was sent.</param>
    /// <param name="present">True when the member was sent.</param>
    internal static Error? OptionalTitle(JsonElement body, out string? title, out bool present)
    {
        title = null;
        present = body.TryGetProperty("title", out _);

        if (!present)
        {
            return null;
        }

        if (RequiredTitle(body, out var required) is { } error)
        {
            return error;
        }

        title = required;

        return null;
    }

    /// <summary>An optional <c>description</c>/<c>precondition</c>: absent is unchanged, null clears, a string replaces.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="member">The member name.</param>
    /// <param name="text">The value when the member was sent (null when it clears the field).</param>
    /// <param name="present">True when the member was sent.</param>
    internal static Error? OptionalLongText(JsonElement body, string member, out string? text, out bool present)
    {
        text = null;
        present = body.TryGetProperty(member, out var value);

        if (!present || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid($"{member} must be a string or null", [new ErrorDetail("/" + member, "invalid_type")]);
        }

        text = value.GetString();

        return AuthoringText.Fits(text, AuthoringContent.MaximumLongTextLength)
            ? null
            : Error.Invalid($"{member} is too long", [new ErrorDetail("/" + member, "too_long")]);
    }

    /// <summary>A required priority of §3.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="priority">The priority on success.</param>
    internal static Error? RequiredPriority(JsonElement body, out string priority)
    {
        priority = string.Empty;

        if (!body.TryGetProperty("priority", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid("priority is required", [new ErrorDetail("/priority", "required")]);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("priority must be a string", [new ErrorDetail("/priority", "invalid_type")]);
        }

        priority = value.GetString()!;

        return AuthoringPriorities.IsValid(priority)
            ? null
            : Error.Invalid(
                "priority is one of critical, high, medium or low (13.1.1 §3)",
                [new ErrorDetail("/priority", "value_invalid")]);
    }

    /// <summary>An optional priority: absent is unchanged.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="priority">The priority when it was sent.</param>
    /// <param name="present">True when the member was sent.</param>
    internal static Error? OptionalPriority(JsonElement body, out string? priority, out bool present)
    {
        priority = null;
        present = body.TryGetProperty("priority", out _);

        if (!present)
        {
            return null;
        }

        if (RequiredPriority(body, out var required) is { } error)
        {
            return error;
        }

        priority = required;

        return null;
    }

    /// <summary>A required <c>state</c> of §6's manual test case matrix.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="state">The state on success.</param>
    internal static Error? RequiredState(JsonElement body, out string state)
    {
        state = string.Empty;

        if (!body.TryGetProperty("state", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid("state is required", [new ErrorDetail("/state", "required")]);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("state must be a string", [new ErrorDetail("/state", "invalid_type")]);
        }

        state = value.GetString()!;

        return ManualTestCaseStateMachine.States.Contains(state, StringComparer.Ordinal)
            ? null
            : Error.Invalid(
                "state is one of draft, approved or retired (13.1.1 §6)",
                [new ErrorDetail("/state", "value_invalid")]);
    }

    /// <summary>An optional 1..500 character <c>reason</c>.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="reason">The reason when it was sent and legal.</param>
    internal static Error? OptionalReason(JsonElement body, out string? reason)
    {
        reason = null;

        if (!body.TryGetProperty("reason", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("reason must be a string", [new ErrorDetail("/reason", "invalid_type")]);
        }

        reason = value.GetString();

        return AuthoringText.CodePointLength(reason) is > 0 and <= ManualCaseCommands.MaximumReasonLength
            ? null
            : Error.Invalid(
                "a reason is 1..500 characters (13.1.2)",
                [new ErrorDetail("/reason", "length_invalid")]);
    }

    /// <summary>
    /// The 422 of a steps verdict: <c>request_invalid</c> with the schema's own <c>/steps/…</c> details, never a path
    /// into the stored document and never any step text (S3).
    /// </summary>
    /// <param name="verdict">The validator's verdict.</param>
    internal static Error? StepsFailure(ContractValidationResult verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.Errors.Count == 0
            ? null
            : Error.Invalid(
                "steps do not match contracts/schemas/authoring-steps.v1.json (13.1.1 §5)",
                [.. verdict.Errors.Select(error => new ErrorDetail(error.Path, error.Reason))]);
    }

    /// <summary>The stored document of a request's <c>steps</c> array: <c>{"steps":[…]}</c>, the shape the column's CHECK requires.</summary>
    /// <param name="steps">The validated array.</param>
    internal static JsonNode? StepsDocument(JsonElement steps) => new JsonObject
    {
        ["steps"] = JsonNode.Parse(steps.GetRawText()),
    };

    /// <summary>The document a stored row already holds, parsed back for a rebuild.</summary>
    /// <param name="document">The row's <c>steps::text</c>.</param>
    internal static JsonNode? ParseDocument(string document) => string.IsNullOrEmpty(document)
        ? null
        : JsonNode.Parse(document);
}
