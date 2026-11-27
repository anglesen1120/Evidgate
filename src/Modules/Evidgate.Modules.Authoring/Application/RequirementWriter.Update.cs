using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>The fields of a requirement that are not its body: what an edit may change next to a new version.</summary>
/// <param name="Kind">One of <see cref="RequirementRules.Kinds"/>.</param>
/// <param name="Title">Title, 1..200 code points after NFC.</param>
/// <param name="ExternalRef">Optional label of the catalog reference alphabet.</param>
/// <param name="FeatureNodeId">Optional live feature node of the same project.</param>
/// <param name="ParentRequirementId">Optional live parent of the same project.</param>
internal sealed record RequirementIdentity(
    string Kind,
    string Title,
    string? ExternalRef,
    Guid? FeatureNodeId,
    Guid? ParentRequirementId);

internal sealed partial class RequirementWriter
{
    /// <summary>
    /// Appends the next version and replaces the identity fields in the same statement. <paramref name="previous"/> is
    /// the identity the caller read: a reference is only looked up again when this edit changes it, so an unrelated
    /// edit is not refused because a node it already pointed at was deleted since.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project the requirement must belong to.</param>
    /// <param name="requirementId">The requirement.</param>
    /// <param name="expectedRowVersion">The row version the caller based the edit on.</param>
    /// <param name="identity">The identity after the edit.</param>
    /// <param name="previous">The identity the caller read.</param>
    /// <param name="body">The new body.</param>
    /// <param name="source">One of <see cref="RequirementRules.Sources"/>.</param>
    /// <param name="authorMembershipId">The author, or null for a service principal.</param>
    /// <param name="reason">Optional reason.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<RequirementHead>> UpdateAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int expectedRowVersion,
        RequirementIdentity identity,
        RequirementIdentity previous,
        string body,
        string source,
        Guid? authorMembershipId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(previous);
        EnsureScope(orgId);

        var shape = CheckIdentityShape(identity);
        if (!shape.IsSuccess)
        {
            return Result.Failure<RequirementHead>(shape.Error!);
        }

        if (CheckVersionFields(body, source, reason) is { } refusal)
        {
            return refusal;
        }

        if (await CheckReferencesAsync(orgId, projectId, requirementId, shape.Value, previous, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return Result.Failure<RequirementHead>(missing);
        }

        return await AppendCoreAsync(orgId, projectId, requirementId, expectedRowVersion, body, source, authorMembershipId, reason, shape.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hides the requirement from the live list. Its versions and head pointer stay, and so does every row that names
    /// it. A missing or already hidden requirement is a 404, a moved row version a 412, and nothing is written then.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project the requirement must belong to.</param>
    /// <param name="requirementId">The requirement.</param>
    /// <param name="expectedRowVersion">The row version the caller read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result> DeleteAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var parameters = new { org_id = orgId, project_id = projectId, id = requirementId, expected_version = expectedRowVersion };
            var deleted = await db.QuerySingleOrDefaultAsync<HeadAdvance>(RequirementSql.SoftDelete, parameters, cancellationToken).ConfigureAwait(false);

            return deleted is not null
                ? Result.Success()
                : Result.Failure(await WhyNothingMatchedAsync(parameters, cancellationToken).ConfigureAwait(false));
        }
        catch (PostgresException exception) when (ToError(exception) is { } error)
        {
            return Result.Failure(error);
        }
    }

    private async ValueTask<Result<RequirementHead>> AppendCoreAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int expectedRowVersion,
        string body,
        string source,
        Guid? authorMembershipId,
        string? reason,
        RequirementIdentity? identity,
        CancellationToken cancellationToken)
    {
        EnsureScope(orgId);

        if (CheckVersionFields(body, source, reason) is { } refusal)
        {
            return refusal;
        }

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var parameters = new
            {
                org_id = orgId,
                project_id = projectId,
                id = requirementId,
                expected_version = expectedRowVersion,
                set_identity = identity is not null,
                kind = identity?.Kind,
                title = identity?.Title,
                external_ref = identity?.ExternalRef,
                feature_node_id = identity?.FeatureNodeId,
                parent_requirement_id = identity?.ParentRequirementId,
            };
            var advanced = await db.QuerySingleOrDefaultAsync<HeadAdvance>(RequirementSql.AdvanceHead, parameters, cancellationToken).ConfigureAwait(false);
            if (advanced is null)
            {
                return Result.Failure<RequirementHead>(await WhyNothingMatchedAsync(parameters, cancellationToken).ConfigureAwait(false));
            }

            var (versionId, hash) = await InsertVersionAsync(orgId, projectId, requirementId, advanced.HeadVersionNo, body, source, authorMembershipId, reason, cancellationToken).ConfigureAwait(false);

            return Result.Success(new RequirementHead(requirementId, versionId, advanced.HeadVersionNo, advanced.Version, hash));
        }
        catch (PostgresException exception) when (ToError(exception) is { } error)
        {
            return Result.Failure<RequirementHead>(error);
        }
    }

    /// <summary>A write that matched no row is a missing or hidden requirement (404) or a row version that moved (412).</summary>
    private async Task<Error> WhyNothingMatchedAsync(object parameters, CancellationToken cancellationToken)
    {
        var state = await db.QuerySingleOrDefaultAsync<HeadState>(RequirementSql.HeadState, parameters, cancellationToken).ConfigureAwait(false);

        return state is null or { Deleted: true }
            ? AuthoringErrors.NotFound("no requirement with that id is visible in this project")
            : AuthoringRequestScope.Stale("requirement", state.Version);
    }

    /// <summary>Title, kind and reference label: the checks that need no database, with the stored (NFC, trimmed) title.</summary>
    private static Result<RequirementIdentity> CheckIdentityShape(RequirementIdentity identity)
    {
        if (!RequirementRules.IsStorable(identity.Title))
        {
            return Result.Failure<RequirementIdentity>(AuthoringErrors.Invalid("/title", "invalid_text", "the title holds a NUL or an unpaired surrogate"));
        }

        var title = AuthoringText.Normalize(identity.Title)?.Trim() ?? string.Empty;
        if (title.Length == 0 || !AuthoringText.Fits(title, RequirementRules.MaximumTitleCodePoints))
        {
            return Result.Failure<RequirementIdentity>(AuthoringErrors.Invalid("/title", "length", "the title must be 1 to 200 characters"));
        }

        if (!RequirementRules.IsKind(identity.Kind))
        {
            return Result.Failure<RequirementIdentity>(AuthoringErrors.Invalid("/kind", "unknown", "the kind is not one of the requirement kinds"));
        }

        if (identity.ExternalRef is not null && !RequirementRules.IsExternalRef(identity.ExternalRef))
        {
            return Result.Failure<RequirementIdentity>(AuthoringErrors.Invalid("/external_ref", "format", "the reference may only use letters, digits and . _ : / # -"));
        }

        return Result.Success(identity with { Title = title });
    }

    /// <summary>
    /// The feature node and the parent must be live rows of this project, looked up only when they differ from
    /// <paramref name="previous"/>. A requirement is never its own parent; a longer loop is refused by the database
    /// trigger and mapped by <see cref="ToError"/>.
    /// </summary>
    private async Task<Error?> CheckReferencesAsync(
        Guid orgId,
        Guid projectId,
        Guid? requirementId,
        RequirementIdentity identity,
        RequirementIdentity? previous,
        CancellationToken cancellationToken)
    {
        if (identity.FeatureNodeId is { } node
            && node != previous?.FeatureNodeId
            && !await ExistsAsync(RequirementSql.FeatureNodeExists, orgId, projectId, node, cancellationToken).ConfigureAwait(false))
        {
            return AuthoringErrors.Invalid("/feature_node_id", "not_found", "no live feature node with that id in this project");
        }

        if (identity.ParentRequirementId is not { } parent || parent == previous?.ParentRequirementId)
        {
            return null;
        }

        if (parent == requirementId)
        {
            return AuthoringErrors.Invalid("/parent_requirement_id", "cycle", "a requirement cannot be its own parent");
        }

        return await ExistsAsync(RequirementSql.RequirementExists, orgId, projectId, parent, cancellationToken).ConfigureAwait(false)
            ? null
            : AuthoringErrors.Invalid("/parent_requirement_id", "not_found", "no live requirement with that id in this project");
    }

    /// <summary>
    /// The database refusals a write can meet: contention (retryable 503), a reference to a row that is not in this
    /// tenant or project (a foreign author membership or project, 422) and the parent loop trigger (422).
    /// </summary>
    private static Error? ToError(PostgresException exception)
    {
        if (AuthoringLockGuard.IsContention(exception))
        {
            return AuthoringErrors.Unavailable();
        }

        if (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return AuthoringErrors.Invalid("/", "reference", "a referenced row does not exist in this tenant and project");
        }

        return exception.SqlState == PostgresErrorCodes.CheckViolation
            && exception.MessageText.StartsWith("requirement_parent_cycle", StringComparison.Ordinal)
                ? AuthoringErrors.Invalid("/parent_requirement_id", "cycle", "the new parent is a descendant of the requirement")
                : null;
    }
}
