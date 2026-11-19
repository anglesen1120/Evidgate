using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Catalog.Application.Queries;

/// <summary>One cursor page of a suite revision's execution plan (<c>GET /v1/suite-revisions/{r}/execution-plan</c>).</summary>
/// <param name="RevisionId">The revision from the route.</param>
/// <param name="Cursor">Opaque cursor of the next page, or null for the first page.</param>
/// <param name="Limit">Page size, 1–500.</param>
internal sealed record GetSuiteExecutionPlanQuery(Guid RevisionId, string? Cursor, int Limit)
    : IQuery<Result<SuiteExecutionPlanPage>>;

/// <summary>
/// Handler of <see cref="GetSuiteExecutionPlanQuery"/>: the revision's project under the ambient tenant scope, then one
/// keyset statement plus one extra row to decide the next page (00 §5.5 C1/C3/C5, P3).
/// <para>
/// The project gate is the revision lookup, so a revision of another project or tenant answers the same
/// <c>404 not_found</c> as an unknown id — never a per-member filter and never an existence oracle. The only 403 is the
/// permission stage's, which runs before this handler and therefore depends on the token alone.
/// </para>
/// <para>
/// A cursor this endpoint issued for another revision or project is <c>422 request_invalid</c>: a page position outside
/// the revision the caller asked for is refused, never clamped. A value that is not this codec's canonical spelling is
/// <c>400</c> — undecodable input, not a valid page position.
/// </para>
/// </summary>
[Permission(Permissions.Suite.PlanRead)]
internal sealed class GetSuiteExecutionPlanQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetSuiteExecutionPlanQuery, Result<SuiteExecutionPlanPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteExecutionPlanPage>> Handle(
        GetSuiteExecutionPlanQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetSuiteExecutionPlanQuery));

        var orgId = scopes.Require().OrgId;
        var projectId = await db.ScalarAsync<Guid?>(
                SuitePlanReadSql.RevisionProject,
                new { org_id = orgId, revision_id = query.RevisionId },
                cancellationToken)
            .ConfigureAwait(false);
        if (projectId is null || CatalogScope.Project(projectId.Value, principals, tenant) is not null)
        {
            return Result.Failure<SuiteExecutionPlanPage>(SuiteErrors.RevisionNotFound);
        }

        var firstPage = query.Cursor is null;
        var cursorOrdinal = 0;
        var cursorMemberId = Guid.Empty;
        if (!firstPage)
        {
            if (!SuitePlanCursor.TryRead(query.Cursor, out var position))
            {
                return Result.Failure<SuiteExecutionPlanPage>(Error.BadRequest(
                    "the cursor is not a page cursor this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            if (position.RevisionId != query.RevisionId || position.ProjectId != projectId.Value)
            {
                return Result.Failure<SuiteExecutionPlanPage>(Error.Invalid(
                    "the cursor was not issued for this suite revision",
                    [new ErrorDetail("cursor", "foreign")]));
            }

            cursorOrdinal = position.Ordinal;
            cursorMemberId = position.MemberId;
        }

        var rows = await db.QueryAsync<SuitePlanRow>(
                firstPage ? SuitePlanReadSql.FirstPage : SuitePlanReadSql.NextPage,
                firstPage
                    ? new { org_id = orgId, revision_id = query.RevisionId, limit = query.Limit + 1 }
                    : new
                    {
                        org_id = orgId,
                        revision_id = query.RevisionId,
                        cursor_ordinal = cursorOrdinal,
                        cursor_id = cursorMemberId,
                        limit = query.Limit + 1,
                    },
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<SuitePlanRow> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new SuiteExecutionPlanPage(
            query.RevisionId,
            [.. page.Select(row => new SuiteExecutionPlanMember(row.CaseId, row.VariantHash, row.CaseRevisionId))],
            hasMore ? SuitePlanCursor.Encode(query.RevisionId, projectId.Value, page[^1].Ordinal, page[^1].Id) : null));
    }
}

/// <summary>One member row of the plan page, joined to its case and variant.</summary>
/// <param name="CaseRevisionId">The pinned case revision.</param>
/// <param name="CaseId">The case that revision belongs to.</param>
/// <param name="VariantHash">The pinned variant's hash.</param>
/// <param name="Ordinal">The member's position in the revision.</param>
/// <param name="Id">The member row's id, the keyset's tie-breaker.</param>
internal sealed record SuitePlanRow(Guid CaseRevisionId, Guid CaseId, string VariantHash, int Ordinal, Guid Id);

/// <summary>
/// The opaque cursor of the execution plan page: <c>v1.&lt;revision&gt;.&lt;project&gt;.&lt;ordinal&gt;.&lt;member id&gt;</c>
/// as unpadded base64url. It is not signed: binding it to the revision and project it names makes a cursor taken from
/// another revision or project refused, and the revision lookup plus forced RLS keep any cursor inside the caller's own
/// tenant. Only this codec's exact spelling is accepted, so a padded or upper-cased variant is undecodable input.
/// </summary>
internal static class SuitePlanCursor
{
    /// <summary>A cursor longer than this cannot be one this codec wrote.</summary>
    private const int MaxLength = 200;

    private const string Prefix = "v1";

    /// <summary>One decoded page position.</summary>
    /// <param name="RevisionId">The revision the cursor was minted for.</param>
    /// <param name="ProjectId">The project that revision belongs to.</param>
    /// <param name="Ordinal">The last row's ordinal.</param>
    /// <param name="MemberId">The last row's member id.</param>
    internal readonly record struct Position(Guid RevisionId, Guid ProjectId, int Ordinal, Guid MemberId);

    /// <summary>Encodes one row's keyset position for a revision of a project.</summary>
    internal static string Encode(Guid revisionId, Guid projectId, int ordinal, Guid memberId) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Payload(revisionId, projectId, ordinal, memberId)));

    /// <summary>
    /// Decodes a cursor this codec wrote. False for anything else — a malformed value, a non-canonical spelling, or a
    /// payload whose rebuilt text differs — so the caller answers 400 rather than guessing a page position.
    /// </summary>
    internal static bool TryRead(string? cursor, out Position position)
    {
        position = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength)
        {
            return false;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = payload.Split('.');
        if (parts.Length != 5
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[1], "D", out var revisionId)
            || !Guid.TryParseExact(parts[2], "D", out var projectId)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal < 0
            || !Guid.TryParseExact(parts[4], "D", out var memberId))
        {
            return false;
        }

        if (!string.Equals(Payload(revisionId, projectId, ordinal, memberId), payload, StringComparison.Ordinal)
            || !string.Equals(Encode(revisionId, projectId, ordinal, memberId), cursor, StringComparison.Ordinal))
        {
            return false;
        }

        position = new Position(revisionId, projectId, ordinal, memberId);
        return true;
    }

    private static string Payload(Guid revisionId, Guid projectId, int ordinal, Guid memberId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}.{revisionId:D}.{projectId:D}.{ordinal}.{memberId:D}");
}
