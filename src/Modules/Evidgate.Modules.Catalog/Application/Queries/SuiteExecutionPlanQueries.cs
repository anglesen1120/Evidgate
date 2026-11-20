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
    : IQuery<Result<SuiteRevisionExecutionPlan>>;

/// <summary>
/// Handler of <see cref="GetSuiteExecutionPlanQuery"/>: one statement answers whether the revision exists under the
/// ambient tenant and the caller's project scope, whether a continuation cursor's anchor is a member of it, and the
/// keyset page itself, one extra row deciding the next page.
/// <para>
/// Both project restrictions — the token's own scope and the request's project — travel as parameters of that
/// statement, so a revision of another project or tenant answers the same <c>404 not_found</c> as an unknown id and
/// none of its members is served or counted; <see cref="CatalogScope.Project"/> re-checks the loaded project as defence
/// in depth. The only 403 is the permission stage's, which runs before this handler and therefore depends on the token
/// alone.
/// </para>
/// <para>
/// A cursor this endpoint issued for another revision or project, and one naming an anchor the revision does not hold,
/// are <c>422 request_invalid</c>: a page position outside the revision the caller asked for is refused, never clamped.
/// A value that is not this codec's canonical spelling is <c>400</c> before the statement runs — undecodable input can
/// never be used to probe whether a revision exists, and the answer is the same for every revision.
/// </para>
/// </summary>
[Permission(Permissions.Suite.PlanRead)]
internal sealed class GetSuiteExecutionPlanQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetSuiteExecutionPlanQuery, Result<SuiteRevisionExecutionPlan>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteRevisionExecutionPlan>> Handle(
        GetSuiteExecutionPlanQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetSuiteExecutionPlanQuery));

        if (ReadPosition(query, out var position) is { } cursorError)
        {
            return Result.Failure<SuiteRevisionExecutionPlan>(cursorError);
        }

        var orgId = scopes.Require().OrgId;
        var tokenProject = principals.Current?.ProjectScope;
        var requestProject = tenant.Current?.ProjectId;
        var rows = await db.QueryAsync<SuitePlanRow>(
                SuitePlanReadSql.Page,
                new
                {
                    org_id = orgId,
                    revision_id = query.RevisionId,
                    token_project_id = tokenProject,
                    request_project_id = requestProject,
                    // The first page anchors before every member: no stored ordinal is negative.
                    cursor_ordinal = position?.Ordinal ?? -1,
                    cursor_id = position?.MemberId ?? Guid.Empty,
                    limit = query.Limit + 1,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return Result.Failure<SuiteRevisionExecutionPlan>(SuiteErrors.RevisionNotFound);
        }

        var projectId = rows[0].ProjectId;
        if (CatalogScope.Project(projectId, principals, tenant) is not null)
        {
            return Result.Failure<SuiteRevisionExecutionPlan>(SuiteErrors.RevisionNotFound);
        }

        if (RefusedCursor(position, query.RevisionId, projectId, rows[0].AnchorPresent) is { } refusal)
        {
            return Result.Failure<SuiteRevisionExecutionPlan>(refusal);
        }

        var members = Served(rows);
        var hasMore = members.Count > query.Limit;
        var page = hasMore ? members.GetRange(0, query.Limit) : members;

        return Result.Success(new SuiteRevisionExecutionPlan(
            query.RevisionId,
            [.. page.Select(served => served.Member)],
            hasMore ? SuitePlanCursor.Encode(query.RevisionId, projectId, page[^1].Ordinal, page[^1].Id) : null));
    }

    /// <summary>The cursor's decoded position, or the 400 of a value this codec did not write; absent for the first page.</summary>
    private static Error? ReadPosition(GetSuiteExecutionPlanQuery query, out SuitePlanCursor.Position? position)
    {
        position = null;

        if (query.Cursor is null)
        {
            return null;
        }

        if (!SuitePlanCursor.TryRead(query.Cursor, out var decoded))
        {
            return Error.BadRequest(
                "the cursor is not a page cursor this endpoint issued",
                [new ErrorDetail("cursor", "malformed")]);
        }

        position = decoded;

        return null;
    }

    /// <summary>
    /// The 422 of a continuation cursor the revision cannot hold: a foreign one names another revision or project, and
    /// one whose anchor is not a member would otherwise silently serve a page from a position that never existed.
    /// </summary>
    private static Error? RefusedCursor(
        SuitePlanCursor.Position? position,
        Guid revisionId,
        Guid projectId,
        bool anchorPresent)
    {
        if (position is not { } anchor)
        {
            return null;
        }

        if (anchor.RevisionId != revisionId || anchor.ProjectId != projectId)
        {
            return Error.Invalid(
                "the cursor was not issued for this suite revision",
                [new ErrorDetail("cursor", "foreign")]);
        }

        return anchorPresent
            ? null
            : Error.Invalid(
                "the cursor's member is not part of this suite revision",
                [new ErrorDetail("cursor", "unknown")]);
    }

    /// <summary>The page's member rows in served order, skipping the null row an empty revision answers with.</summary>
    private static List<ServedMember> Served(IReadOnlyList<SuitePlanRow> rows) =>
    [
        .. rows
            .Select(row => row.CaseId is { } caseId
                && row.CaseRevisionId is { } caseRevisionId
                && row.VariantHash is { } variantHash
                && row.Ordinal is { } ordinal
                && row.Id is { } id
                    ? new ServedMember(new SuiteRevisionExecutionPlanMember(caseId, variantHash, caseRevisionId), ordinal, id)
                    : (ServedMember?)null)
            .OfType<ServedMember>(),
    ];
}

/// <summary>
/// One row of the plan statement. It is a mutable class, not a record: the statement projects the revision's project
/// and the anchor verdict beside the member columns, and Dapper binds a class by column name through
/// <c>DefaultTypeMap.MatchNamesWithUnderscores</c>. The member columns are null exactly when the revision holds no
/// member on this page, which is how an empty revision still answers 200.
/// </summary>
internal sealed class SuitePlanRow
{
    /// <summary>The revision's project, the project gate's input.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Whether a continuation cursor's <c>(ordinal, id)</c> names a member of the revision.</summary>
    public bool AnchorPresent { get; set; }

    /// <summary>The pinned case revision.</summary>
    public Guid? CaseRevisionId { get; set; }

    /// <summary>The case that revision belongs to.</summary>
    public Guid? CaseId { get; set; }

    /// <summary>The pinned variant's hash.</summary>
    public string? VariantHash { get; set; }

    /// <summary>The member's position in the revision.</summary>
    public int? Ordinal { get; set; }

    /// <summary>The member row's id, the keyset's tie-breaker.</summary>
    public Guid? Id { get; set; }
}

/// <summary>A served member with the position the next page's cursor is minted from.</summary>
/// <param name="Member">The member as the response carries it.</param>
/// <param name="Ordinal">The member's stored ordinal.</param>
/// <param name="Id">The member row's id.</param>
internal readonly record struct ServedMember(SuiteRevisionExecutionPlanMember Member, int Ordinal, Guid Id);

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
