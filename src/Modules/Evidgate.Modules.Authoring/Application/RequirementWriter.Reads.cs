using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>A requirement with its head, as the scoped read answers it.</summary>
internal sealed class RequirementRow
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? ExternalRef { get; set; }

    public Guid? FeatureNodeId { get; set; }

    public Guid? ParentRequirementId { get; set; }

    public int HeadVersionNo { get; set; }

    /// <summary>The identity row's version: the <c>If-Match</c> token of the next write.</summary>
    public int Version { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid HeadVersionId { get; set; }

    /// <summary>The head version's body, so an edit that leaves the body alone can carry it into the next version.</summary>
    public string Body { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>One row of the live list: identity and head numbers, never the body.</summary>
internal sealed class RequirementListRow
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? ExternalRef { get; set; }

    public Guid? FeatureNodeId { get; set; }

    public Guid? ParentRequirementId { get; set; }

    public int HeadVersionNo { get; set; }

    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>One immutable version as the history read answers it.</summary>
internal sealed class RequirementVersionRow
{
    public Guid Id { get; set; }

    public int VersionNo { get; set; }

    public string Body { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public Guid? AuthorMembershipId { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}

internal sealed partial class RequirementWriter
{
    /// <summary>The page size ceiling of <see cref="ListVersionsAsync"/> and <see cref="ListLiveAsync"/>.</summary>
    internal const int MaximumPageSize = 200;

    /// <summary>
    /// The most body bytes one history page carries (the first row is always returned). Twenty thousand code points
    /// can be 80 KB, so the row cap alone would allow a 16 MB page.
    /// </summary>
    internal const int MaximumVersionPageBytes = 262_144;

    /// <summary>The requirement with its head, or a 404 when it is not visible in this project. A deleted one is still readable by id.</summary>
    internal async ValueTask<Result<RequirementRow>> GetAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        var row = await db.QuerySingleOrDefaultAsync<RequirementRow>(
            RequirementSql.SelectRequirement,
            new { org_id = orgId, project_id = projectId, id = requirementId },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<RequirementRow>(AuthoringErrors.NotFound("no requirement with that id is visible in this project"))
            : Result.Success(row);
    }

    /// <summary>
    /// A page of the requirement's history, newest first. <paramref name="beforeVersionNo"/> is the keyset: pass the
    /// last number of the previous page. The history of a soft-deleted requirement stays readable.
    /// </summary>
    internal async ValueTask<Result<IReadOnlyList<RequirementVersionRow>>> ListVersionsAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int? beforeVersionNo,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        if (limit is < 1 or > MaximumPageSize)
        {
            return Result.Failure<IReadOnlyList<RequirementVersionRow>>(
                AuthoringErrors.Invalid("/limit", "range", "the page size must be 1 to 200"));
        }

        var versions = await db.QueryAsync<RequirementVersionRow>(
            RequirementSql.SelectVersions,
            new
            {
                org_id = orgId,
                project_id = projectId,
                requirement_id = requirementId,
                before_version_no = beforeVersionNo,
                limit,
                max_bytes = MaximumVersionPageBytes,
            },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(versions);
    }

    /// <summary>One keyset page of the project's live requirements, oldest first; the caller asks for one row more than it shows.</summary>
    internal async ValueTask<Result<IReadOnlyList<RequirementListRow>>> ListLiveAsync(
        Guid orgId,
        Guid projectId,
        DateTimeOffset? afterCreatedAt,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        if (limit is < 1 or > MaximumPageSize + 1)
        {
            return Result.Failure<IReadOnlyList<RequirementListRow>>(
                AuthoringErrors.Invalid("/limit", "range", "the page size must be 1 to 200"));
        }

        var rows = await db.QueryAsync<RequirementListRow>(
            RequirementSql.SelectLive,
            new { org_id = orgId, project_id = projectId, after_created_at = afterCreatedAt, after_id = afterId, limit },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(rows);
    }

    /// <summary>One version of this requirement, or a 404 when the id names no version of it (a sibling's version is not visible here).</summary>
    internal async ValueTask<Result<RequirementVersionRow>> GetVersionAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        var row = await db.QuerySingleOrDefaultAsync<RequirementVersionRow>(
            RequirementSql.SelectVersion,
            new { org_id = orgId, project_id = projectId, requirement_id = requirementId, id = versionId },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<RequirementVersionRow>(AuthoringErrors.NotFound("no version with that id belongs to this requirement"))
            : Result.Success(row);
    }
}
