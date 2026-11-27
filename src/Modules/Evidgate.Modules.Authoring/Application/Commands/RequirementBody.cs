using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// The members a create or an edit sent. A member that cannot be null (<c>kind</c>, <c>title</c>, <c>body</c>) is null
/// here when absent; the three clearable members carry a presence flag, because absent keeps the value and an explicit
/// null clears it.
/// </summary>
/// <param name="Kind">The kind, or null when absent.</param>
/// <param name="Title">The title, or null when absent.</param>
/// <param name="Body">The body, or null when absent.</param>
/// <param name="Reason">The reason, or null.</param>
/// <param name="HasExternalRef">True when <c>external_ref</c> was sent.</param>
/// <param name="ExternalRef">The label sent, null to clear.</param>
/// <param name="HasFeatureNode">True when <c>feature_node_id</c> was sent.</param>
/// <param name="FeatureNodeId">The node sent, null to clear.</param>
/// <param name="HasParent">True when <c>parent_requirement_id</c> was sent.</param>
/// <param name="ParentRequirementId">The parent sent, null to make the requirement a root.</param>
internal sealed record RequirementFields(
    string? Kind,
    string? Title,
    string? Body,
    string? Reason,
    bool HasExternalRef,
    string? ExternalRef,
    bool HasFeatureNode,
    Guid? FeatureNodeId,
    bool HasParent,
    Guid? ParentRequirementId)
{
    /// <summary>True when the request names at least one member an edit can change; a reason alone changes nothing.</summary>
    internal bool ChangesAnything =>
        Kind is not null || Title is not null || Body is not null || HasExternalRef || HasFeatureNode || HasParent;
}

/// <summary>Reads the requirement route bodies member by member, so each type mistake has its own 422 path.</summary>
internal static class RequirementBody
{
    /// <summary>The members of <c>POST …/requirements</c> and <c>PATCH …/requirements/{r}</c>.</summary>
    internal static readonly string[] WriteMembers =
        ["kind", "title", "body", "reason", "external_ref", "feature_node_id", "parent_requirement_id"];

    /// <summary>The members of <c>POST …/requirements/{r}/restore</c>.</summary>
    internal static readonly string[] RestoreMembers = ["version_id", "reason"];

    /// <summary>Reads a create (<paramref name="create"/>: kind and title are required) or an edit body.</summary>
    /// <param name="body">The strict JSON object the route read.</param>
    /// <param name="create">True for a create.</param>
    internal static Result<RequirementFields> Read(JsonElement body, bool create)
    {
        if (Text(body, "kind", false, out var kind) is { } kindError)
        {
            return Result.Failure<RequirementFields>(kindError);
        }

        if (Text(body, "title", false, out var title) is { } titleError)
        {
            return Result.Failure<RequirementFields>(titleError);
        }

        if (Text(body, "body", false, out var text) is { } bodyError)
        {
            return Result.Failure<RequirementFields>(bodyError);
        }

        if (Text(body, "reason", true, out var reason) is { } reasonError)
        {
            return Result.Failure<RequirementFields>(reasonError);
        }

        if (Text(body, "external_ref", true, out var externalRef) is { } refError)
        {
            return Result.Failure<RequirementFields>(refError);
        }

        if (Id(body, "feature_node_id", out var node) is { } nodeError)
        {
            return Result.Failure<RequirementFields>(nodeError);
        }

        if (Id(body, "parent_requirement_id", out var parent) is { } parentError)
        {
            return Result.Failure<RequirementFields>(parentError);
        }

        if (create && kind is null)
        {
            return Result.Failure<RequirementFields>(Required("kind"));
        }

        if (create && title is null)
        {
            return Result.Failure<RequirementFields>(Required("title"));
        }

        return Result.Success(new RequirementFields(
            kind,
            title,
            text,
            reason,
            body.TryGetProperty("external_ref", out _),
            externalRef,
            body.TryGetProperty("feature_node_id", out _),
            node,
            body.TryGetProperty("parent_requirement_id", out _),
            parent));
    }

    /// <summary>Reads a restore body: the version to restore and the reason.</summary>
    /// <param name="body">The strict JSON object the route read.</param>
    internal static Result<(Guid VersionId, string Reason)> ReadRestore(JsonElement body)
    {
        if (ManualCaseBody.RequiredId(body, "version_id", out var versionId) is { } idError)
        {
            return Result.Failure<(Guid, string)>(idError);
        }

        if (Text(body, "reason", false, out var reason) is { } reasonError)
        {
            return Result.Failure<(Guid, string)>(reasonError);
        }

        return reason is null
            ? Result.Failure<(Guid, string)>(Required("reason"))
            : Result.Success((versionId, reason));
    }

    private static Error Required(string member) =>
        Error.Invalid($"{member} is required", [new ErrorDetail("/" + member, "required")]);

    private static Error? Text(JsonElement body, string member, bool nullable, out string? value)
    {
        value = null;
        if (!body.TryGetProperty(member, out var element) || (nullable && element.ValueKind == JsonValueKind.Null))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid($"{member} must be a string", [new ErrorDetail("/" + member, "invalid_type")]);
        }

        value = element.GetString();

        return null;
    }

    private static Error? Id(JsonElement body, string member, out Guid? value)
    {
        value = null;
        if (!body.TryGetProperty(member, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out var id) || id == Guid.Empty)
        {
            return Error.Invalid($"{member} must be a uuid or null", [new ErrorDetail("/" + member, "value_invalid")]);
        }

        value = id;

        return null;
    }
}
