using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Domain;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Policy.Application.DeployTargets;

/// <summary>Wire shape of a deploy target.</summary>
/// <param name="Id">Target id (UUIDv7).</param>
/// <param name="ProjectId">The project the target belongs to.</param>
/// <param name="Name">The slug, unique per project ignoring case.</param>
/// <param name="Order">Display position, 0-10000.</param>
/// <param name="PolicyRevisionId">The pinned policy revision of the same project, or null for the active one.</param>
/// <param name="State"><c>active</c> or <c>disabled</c>.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Version">The row version, also the <c>ETag</c>.</param>
internal sealed record DeployTargetDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    int Order,
    Guid? PolicyRevisionId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>The project's targets in display order; one unpaged page, bounded by <see cref="DeployTargetRules.MaxPerProject"/>.</summary>
/// <param name="Items">Targets ordered by <c>order</c>, then name.</param>
/// <param name="NextCursor">Always null: the list never exceeds one page.</param>
internal sealed record DeployTargetPage(IReadOnlyList<DeployTargetDto> Items, string? NextCursor);

/// <summary>Body of <c>POST /v1/projects/{p}/deploy-targets</c>.</summary>
/// <param name="Name">The slug.</param>
/// <param name="Order">Display position; defaults to 0.</param>
/// <param name="PolicyRevisionId">Optional pin to a policy revision of this project.</param>
internal sealed record DeployTargetCreateRequest(string? Name, int? Order, Guid? PolicyRevisionId);

/// <summary>Errors of the deploy-target routes (00 §9.2).</summary>
internal static class DeployTargetErrors
{
    internal static Error NotFound => Error.NotFound("no deploy target with that id is visible in this project");

    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    internal static Error Exists => Error.Conflict(
        ApiErrorCode.TargetExists,
        "a deploy target with that name already exists in this project",
        [new ErrorDetail("name", "exists")]);

    internal static Error AlreadyDisabled => Error.Conflict(
        ApiErrorCode.InvalidTransition,
        "the deploy target is already disabled");

    internal static Error UnknownPolicyRevision => Error.Invalid(
        "the policy revision is not a revision of this project",
        [new ErrorDetail("policy_revision_id", "unknown")]);

    internal static Error TooMany => Error.Invalid(
        string.Create(CultureInfo.InvariantCulture, $"a project holds at most {DeployTargetRules.MaxPerProject} deploy targets"),
        [new ErrorDetail("project_id", "too_many_targets")]);

    internal static Error NothingToChange => Error.Invalid(
        "the patch names no field to change",
        [new ErrorDetail(string.Empty, "empty_patch")]);

    internal static Error Stale(int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        string.Create(CultureInfo.InvariantCulture, $"the deploy target changed since it was read (current version {currentVersion})"),
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details:
        [
            new ErrorDetail("If-Match", "stale"),
            new ErrorDetail("version", currentVersion.ToString(CultureInfo.InvariantCulture)),
        ]);
}

/// <summary>Field checks shared by the create and the patch.</summary>
internal static class DeployTargetValidation
{
    /// <summary>One <see cref="ErrorDetail"/> per offending field, or null when every given field is acceptable.</summary>
    internal static Error? Check(string? name, bool nameGiven, int? order, bool pinGiven, Guid? pin, string? state)
    {
        var details = new List<ErrorDetail>();

        if (nameGiven && !DeployTargetRules.IsValidName(name))
        {
            details.Add(new ErrorDetail("name", name is null ? "required" : "invalid_value"));
        }

        if (order is < 0 or > DeployTargetRules.MaxOrder)
        {
            details.Add(new ErrorDetail("order", "out_of_range"));
        }

        if (pinGiven && pin == Guid.Empty)
        {
            details.Add(new ErrorDetail("policy_revision_id", "invalid_value"));
        }

        if (state is not null && !DeployTargetState.IsKnown(state))
        {
            details.Add(new ErrorDetail("state", "invalid_value"));
        }

        return details.Count == 0 ? null : Error.Invalid("the deploy target request is not valid", details);
    }
}

/// <summary>
/// Parses <c>If-Match</c> into the row version: the strong and weak quoted forms and a bare number. An absent or
/// unparsable header is 422 (00 §9.1 makes it required on every PATCH and DELETE).
/// </summary>
internal static class DeployTargetPreconditions
{
    internal static Error? IfMatch(string? raw, out int version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error.Invalid(
                "If-Match is required: send the ETag of the deploy target you read (00 §9.1)",
                [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return int.TryParse(value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1
            ? null
            : Error.Invalid(
                "If-Match must be the ETag this endpoint issued: the deploy target's version (00 §9.1)",
                [new ErrorDetail("If-Match", "invalid")]);
    }
}

/// <summary>The Dapper statements of the deploy-target routes: each is one parameterised statement (00 §5.5 C5).</summary>
internal static class DeployTargetSql
{
    private const string Columns = """
               id, project_id, name, position as "order", policy_revision_id, state, created_at, updated_at, version
        """;

    /// <summary>
    /// The project row locked <c>FOR NO KEY UPDATE</c> (<c>evidgate_app</c> holds table-level UPDATE on project), so two
    /// concurrent creates of one project run one after the other and the per-project cap is exact. A missing or
    /// soft-deleted project reads no row.
    /// </summary>
    internal const string LockProject = """
        select 1
          from public.project
         where id = @project_id
           and status <> 'deleted'
           for no key update
        """;

    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    internal const string Count = "select count(*) from public.deploy_target where org_id = @org_id and project_id = @project_id";

    /// <summary>The pin must be a revision of the target's own project; the composite FK is the backstop.</summary>
    internal const string PinExists = """
        select 1
          from public.policy_revision
         where org_id = @org_id
           and project_id = @project_id
           and id = @policy_revision_id
        """;

    internal const string Insert = $"""
        insert into public.deploy_target (id, org_id, project_id, name, position, policy_revision_id)
        values (@id, @org_id, @project_id, @name, @position, @policy_revision_id)
        returning {Columns}
        """;

    internal const string Get = $"""
        select {Columns}
          from public.deploy_target
         where org_id = @org_id
           and project_id = @project_id
           and id = @id
           and exists (select 1 from public.project where id = @project_id and status <> 'deleted')
        """;

    internal const string List = $"""
        select {Columns}
          from public.deploy_target
         where org_id = @org_id
           and project_id = @project_id
         order by position,
                  lower(name),
                  id
         limit @limit
        """;

    /// <summary>
    /// The versioned write: the version is part of the predicate, so a stale writer matches nothing and loses atomically.
    /// A null argument leaves its column unchanged; the pin is the one column that may be set to null on purpose.
    /// </summary>
    internal const string Update = $"""
        update public.deploy_target
           set name = coalesce(@name::text, name),
               position = coalesce(@position::integer, position),
               policy_revision_id = case when @set_pin then @policy_revision_id::uuid else policy_revision_id end,
               state = coalesce(@state::text, state)
         where org_id = @org_id
           and project_id = @project_id
           and id = @id
           and version = @version
           and exists (select 1 from public.project where id = @project_id and status <> 'deleted')
        returning {Columns}
        """;
}
