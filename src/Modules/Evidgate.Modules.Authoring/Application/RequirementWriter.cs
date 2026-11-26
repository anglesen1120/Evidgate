using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>The fields of a new requirement and its first version.</summary>
/// <param name="ProjectId">The project the requirement belongs to.</param>
/// <param name="Kind">One of <see cref="RequirementRules.Kinds"/>.</param>
/// <param name="Title">Title, 1..200 code points after NFC.</param>
/// <param name="ExternalRef">Optional label of the catalog reference alphabet.</param>
/// <param name="FeatureNodeId">Optional live feature node of the same project.</param>
/// <param name="ParentRequirementId">Optional live parent of the same project.</param>
/// <param name="Body">First body; may be empty, at most 20 000 code points after NFC.</param>
/// <param name="Source">One of <see cref="RequirementRules.Sources"/>.</param>
/// <param name="AuthorMembershipId">The author, or null for a service principal.</param>
/// <param name="Reason">Optional reason, 1..500 code points.</param>
internal sealed record RequirementCreate(
    Guid ProjectId,
    string Kind,
    string Title,
    string? ExternalRef,
    Guid? FeatureNodeId,
    Guid? ParentRequirementId,
    string Body,
    string Source,
    Guid? AuthorMembershipId,
    string? Reason);

/// <summary>The head a write produced: the version it appended and the row version to send as the next <c>If-Match</c>.</summary>
/// <param name="RequirementId">The stable identity.</param>
/// <param name="VersionId">The appended version row.</param>
/// <param name="VersionNo">Its number, which is also the new head number.</param>
/// <param name="RowVersion">The identity row's version after the write (the concurrency token).</param>
/// <param name="ContentHash">The body hash of the appended version.</param>
internal sealed record RequirementHead(Guid RequirementId, Guid VersionId, int VersionNo, int RowVersion, string ContentHash);

/// <summary>
/// The one code path that creates a requirement or appends a version (migration 0132). It never opens, commits or
/// rolls back a transaction: both rows of a write run on the caller's tenant transaction, so a refusal or rollback
/// leaves neither a moved head nor an orphan version. Two writers of one requirement serialize on the row lock of
/// <see cref="RequirementSql.AdvanceHead"/>; the loser sees a moved row version and is answered as stale.
/// </summary>
internal sealed partial class RequirementWriter(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Creates a requirement and its version 1.</summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="create">The fields.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<RequirementHead>> CreateAsync(
        Guid orgId,
        RequirementCreate create,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        EnsureScope(orgId);

        if (!RequirementRules.IsStorable(create.Title))
        {
            return Refuse("/title", "invalid_text", "the title holds a NUL or an unpaired surrogate");
        }

        var title = AuthoringText.Normalize(create.Title)?.Trim() ?? string.Empty;
        if (title.Length == 0 || !AuthoringText.Fits(title, RequirementRules.MaximumTitleCodePoints))
        {
            return Refuse("/title", "length", "the title must be 1 to 200 characters");
        }

        if (!RequirementRules.IsKind(create.Kind))
        {
            return Refuse("/kind", "unknown", "the kind is not one of the requirement kinds");
        }

        if (create.ExternalRef is not null && !RequirementRules.IsExternalRef(create.ExternalRef))
        {
            return Refuse("/external_ref", "format", "the reference may only use letters, digits and . _ : / # -");
        }

        if (CheckVersionFields(create.Body, create.Source, create.Reason) is { } refusal)
        {
            return refusal;
        }

        if (create.FeatureNodeId is { } node && !await ExistsAsync(RequirementSql.FeatureNodeExists, orgId, create.ProjectId, node, cancellationToken).ConfigureAwait(false))
        {
            return Refuse("/feature_node_id", "not_found", "no live feature node with that id in this project");
        }

        if (create.ParentRequirementId is { } parent && !await ExistsAsync(RequirementSql.RequirementExists, orgId, create.ProjectId, parent, cancellationToken).ConfigureAwait(false))
        {
            return Refuse("/parent_requirement_id", "not_found", "no live requirement with that id in this project");
        }

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var requirementId = Guid.CreateVersion7();
            var rowVersion = await db.ScalarAsync<int>(
                RequirementSql.InsertRequirement,
                new
                {
                    id = requirementId,
                    org_id = orgId,
                    project_id = create.ProjectId,
                    kind = create.Kind,
                    title,
                    external_ref = create.ExternalRef,
                    feature_node_id = create.FeatureNodeId,
                    parent_requirement_id = create.ParentRequirementId,
                },
                cancellationToken).ConfigureAwait(false);

            var (versionId, hash) = await InsertVersionAsync(orgId, create.ProjectId, requirementId, 1, create.Body, create.Source, create.AuthorMembershipId, create.Reason, cancellationToken).ConfigureAwait(false);

            return Result.Success(new RequirementHead(requirementId, versionId, 1, rowVersion, hash));
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<RequirementHead>(AuthoringErrors.Unavailable());
        }
    }

    /// <summary>
    /// Appends the next version of a requirement. <paramref name="expectedRowVersion"/> is the row version the caller
    /// read (the <c>If-Match</c>); a restore is this call with the restored body, so history only ever grows.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project the requirement must belong to.</param>
    /// <param name="requirementId">The requirement.</param>
    /// <param name="expectedRowVersion">The row version the caller based the edit on.</param>
    /// <param name="body">The new body.</param>
    /// <param name="source">One of <see cref="RequirementRules.Sources"/>.</param>
    /// <param name="authorMembershipId">The author, or null for a service principal.</param>
    /// <param name="reason">Optional reason.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<RequirementHead>> AppendVersionAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int expectedRowVersion,
        string body,
        string source,
        Guid? authorMembershipId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        if (CheckVersionFields(body, source, reason) is { } refusal)
        {
            return refusal;
        }

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var parameters = new { org_id = orgId, project_id = projectId, id = requirementId, expected_version = expectedRowVersion };
            var advanced = await db.QuerySingleOrDefaultAsync<HeadAdvance>(RequirementSql.AdvanceHead, parameters, cancellationToken).ConfigureAwait(false);
            if (advanced is null)
            {
                var state = await db.QuerySingleOrDefaultAsync<HeadState>(RequirementSql.HeadState, parameters, cancellationToken).ConfigureAwait(false);

                return state is null or { Deleted: true }
                    ? Result.Failure<RequirementHead>(AuthoringErrors.NotFound("no requirement with that id is visible in this project"))
                    : Result.Failure<RequirementHead>(AuthoringRequestScope.Stale("requirement", state.Version));
            }

            var (versionId, hash) = await InsertVersionAsync(orgId, projectId, requirementId, advanced.HeadVersionNo, body, source, authorMembershipId, reason, cancellationToken).ConfigureAwait(false);

            return Result.Success(new RequirementHead(requirementId, versionId, advanced.HeadVersionNo, advanced.Version, hash));
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<RequirementHead>(AuthoringErrors.Unavailable());
        }
    }

    private async Task<(Guid VersionId, string Hash)> InsertVersionAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int versionNo,
        string body,
        string source,
        Guid? authorMembershipId,
        string? reason,
        CancellationToken cancellationToken)
    {
        RequirementRules.TryNormalizeBody(body, out var normalized);
        var hash = RequirementRules.ContentHash(normalized);
        var versionId = Guid.CreateVersion7();

        await db.ExecuteAsync(
            RequirementSql.InsertVersion,
            new
            {
                id = versionId,
                org_id = orgId,
                project_id = projectId,
                requirement_id = requirementId,
                version_no = versionNo,
                body = normalized,
                content_hash = hash,
                source,
                author_membership_id = authorMembershipId,
                reason = AuthoringText.Normalize(reason),
            },
            cancellationToken).ConfigureAwait(false);

        return (versionId, hash);
    }

    /// <summary>Validates the three fields every version carries; the body is checked after NFC.</summary>
    private static Result<RequirementHead>? CheckVersionFields(string? body, string? source, string? reason)
    {
        if (!RequirementRules.IsStorable(body) || !RequirementRules.IsStorable(reason))
        {
            return Refuse(RequirementRules.IsStorable(body) ? "/reason" : "/body", "invalid_text", "the text holds a NUL or an unpaired surrogate");
        }

        if (body is null)
        {
            return Refuse("/body", "required", "the body is required");
        }

        if (!RequirementRules.TryNormalizeBody(body, out _))
        {
            return Refuse("/body", "length", "the body is longer than 20000 characters");
        }

        if (!RequirementRules.IsSource(source))
        {
            return Refuse("/source", "unknown", "the source is not one of manual, import, tracker, ai");
        }

        var normalizedReason = AuthoringText.Normalize(reason);
        if (normalizedReason is not null
            && (normalizedReason.Length == 0 || !AuthoringText.Fits(normalizedReason, RequirementRules.MaximumReasonCodePoints)))
        {
            return Refuse("/reason", "length", "the reason must be 1 to 500 characters");
        }

        return null;
    }

    private static Result<RequirementHead> Refuse(string path, string reason, string message) =>
        Result.Failure<RequirementHead>(AuthoringErrors.Invalid(path, reason, message));

    private async Task<bool> ExistsAsync(string sql, Guid orgId, Guid projectId, Guid id, CancellationToken cancellationToken) =>
        await db.ScalarAsync<bool>(sql, new { org_id = orgId, project_id = projectId, id }, cancellationToken).ConfigureAwait(false);

    private void EnsureScope(Guid orgId)
    {
        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }
    }

    private sealed class HeadAdvance
    {
        public int HeadVersionNo { get; set; }

        public int Version { get; set; }
    }

    private sealed class HeadState
    {
        public int Version { get; set; }

        public bool Deleted { get; set; }
    }
}
