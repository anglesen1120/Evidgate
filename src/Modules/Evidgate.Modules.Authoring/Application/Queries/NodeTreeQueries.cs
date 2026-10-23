using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// The feature tree of one project (<c>GET /v1/projects/{p}/authoring/tree</c>, 13.1.1 §5 route 1): one sibling page,
/// or — when <paramref name="NodeId"/> is given — that node, its ancestor path and its live children.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ParentId">The parent whose live children to list, or null.</param>
/// <param name="NodeId">The node to address, or null; takes precedence over <paramref name="ParentId"/>, which the route refuses alongside it.</param>
/// <param name="IncludePath">True when <c>include=path</c> asked for the breadcrumb.</param>
/// <param name="Cursor">The opaque <c>(position, id)</c> cursor, or null for the first page.</param>
/// <param name="Limit">The page size, 1..200.</param>
internal sealed record GetAuthoringTreeQuery(
    Guid ProjectId,
    Guid? ParentId,
    Guid? NodeId,
    bool IncludePath,
    string? Cursor,
    int Limit)
    : IQuery<Result<AuthoringTreeView>>;

/// <summary>
/// The answer of <see cref="GetAuthoringTreeQuery"/> plus the value the route turns into the <c>ETag</c> header: the
/// order tag of the listing group (<see cref="AuthoringHashes.OrderHash"/>), which is exactly what route 4's
/// <c>If-Match: "order:&lt;sha256&gt;"</c> must carry. The tag is <b>not</b> a member of the wire body — the §5 shape of
/// route 1 is closed — so it travels beside it and the endpoint sets the header, the way the catalog's suite read turns
/// its head revision into an <c>ETag</c>.
/// </summary>
/// <param name="Body">The documented response.</param>
/// <param name="OrderHash">The <c>order:sha256:&lt;hex&gt;</c> tag of the listed sibling group.</param>
internal sealed record AuthoringTreeView(AuthoringTreeResponse Body, string OrderHash);

/// <summary>
/// Handler of <see cref="GetAuthoringTreeQuery"/>: the project gate, then at most five statements (project liveness, the
/// addressed node or the page, the ancestors, one counts statement for the whole page, the order tag) in a
/// <b>read-only</b> transaction. The page itself is one keyset statement over <c>feature_node_tree_idx</c>.
/// </summary>
/// <remarks>
/// <b>Roots versus children, and why <c>parent_id is null</c> is not written as a parameter</b> (OI-13-112): the two
/// groups use two statements so each one's predicate is a searchable form of the index; the alternation happens here, in
/// C#, not inside the SQL text (00 §5.7 S1: parameters are values, never SQL fragments).
/// </remarks>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetAuthoringTreeQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetAuthoringTreeQuery, Result<AuthoringTreeView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringTreeView>> Handle(GetAuthoringTreeQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetAuthoringTreeQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<AuthoringTreeView>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = query.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<AuthoringTreeView>(AuthoringRules.ProjectNotFound);
        }

        // The group the page lists, and the node the answer names (route 1's `node` member is populated for node_id only).
        AuthoringNodeReadSql.NodePageRow? addressed = null;
        AuthoringNodeReadSql.NodePageRow? groupNode = null;
        Guid? groupParent;

        if (query.NodeId is { } nodeId)
        {
            addressed = await db.QuerySingleOrDefaultAsync<AuthoringNodeReadSql.NodePageRow>(
                AuthoringNodeReadSql.LiveNode,
                new { org_id = orgId, project_id = query.ProjectId, id = nodeId },
                cancellationToken).ConfigureAwait(false);

            if (addressed is null)
            {
                return Result.Failure<AuthoringTreeView>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
            }

            groupNode = addressed;
            groupParent = addressed.Id;
        }
        else if (query.ParentId is { } parentId)
        {
            // A parent that is gone is the same 404 as a node that never existed (AC10), so the page can never be
            // listed for a tombstone.
            groupNode = await db.QuerySingleOrDefaultAsync<AuthoringNodeReadSql.NodePageRow>(
                AuthoringNodeReadSql.LiveNode,
                new { org_id = orgId, project_id = query.ProjectId, id = parentId },
                cancellationToken).ConfigureAwait(false);

            if (groupNode is null)
            {
                return Result.Failure<AuthoringTreeView>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
            }

            groupParent = parentId;
        }
        else
        {
            groupParent = null;
        }

        int? cursorPosition = null;
        Guid? cursorId = null;
        if (query.Cursor is not null)
        {
            if (!AuthoringNodeCursor.TryDecode(query.Cursor, out var decodedPosition, out var decodedId))
            {
                return Result.Failure<AuthoringTreeView>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("/cursor", "malformed")]));
            }

            cursorPosition = decodedPosition;
            cursorId = decodedId;
        }

        var rows = await PageAsync(orgId, query, groupParent, cursorPosition, cursorId, cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = hasMore ? rows.Take(query.Limit).ToList() : rows;

        // One counts statement for the whole page, never one per node; the addressed node joins the same statement so
        // its own counts cost nothing extra.
        var countIds = page.Select(row => row.Id).ToList();
        if (addressed is not null)
        {
            countIds.Add(addressed.Id);
        }

        var counts = countIds.Count == 0
            ? []
            : await db.QueryAsync<AuthoringNodeReadSql.NodeCountRow>(
                AuthoringNodeReadSql.Counts,
                new { org_id = orgId, project_id = query.ProjectId, ids = countIds.ToArray() },
                cancellationToken).ConfigureAwait(false);

        var byId = counts.ToDictionary(row => row.NodeId, row => row);

        var items = page.Select(row => ToDto(row, byId)).ToList();

        var node = addressed is null
            ? null
            : ToDto(
                new AuthoringNodeReadSql.NodePageRow
                {
                    Id = addressed.Id,
                    ParentId = addressed.ParentId,
                    Kind = addressed.Kind,
                    Title = addressed.Title,
                    Position = addressed.Position,
                    Version = addressed.Version,
                },
                byId);

        var path = await PathAsync(orgId, query, groupParent, groupNode, query.NodeId is null && groupNode is not null, cancellationToken).ConfigureAwait(false);

        var order = await db.QueryAsync<AuthoringNodeReadSql.SiblingVersionRow>(
            groupParent is null ? AuthoringNodeReadSql.SiblingVersionsRoot : AuthoringNodeReadSql.SiblingVersionsChild,
            new { org_id = orgId, project_id = query.ProjectId, parent_id = groupParent },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(new AuthoringTreeView(
            new AuthoringTreeResponse(
                items,
                hasMore ? AuthoringNodeCursor.Encode(page[^1].Position, page[^1].Id) : null,
                node,
                path),
            AuthoringHashes.OrderHash(order.Select(row => (row.Id, row.Version)))));
    }

    private async Task<List<AuthoringNodeReadSql.NodePageRow>> PageAsync(
        Guid orgId,
        GetAuthoringTreeQuery query,
        Guid? groupParent,
        int? cursorPosition,
        Guid? cursorId,
        CancellationToken cancellationToken)
    {
        var parameters = cursorPosition is { } position && cursorId is { } id
            ? (object)new { org_id = orgId, project_id = query.ProjectId, parent_id = groupParent, cursor_position = position, cursor_id = id, limit = query.Limit + 1 }
            : new { org_id = orgId, project_id = query.ProjectId, parent_id = groupParent, limit = query.Limit + 1 };

        var root = groupParent is null;
        var sql = cursorPosition is not null
            ? (root ? AuthoringNodeReadSql.RootsNextPage : AuthoringNodeReadSql.ChildrenNextPage)
            : (root ? AuthoringNodeReadSql.RootsFirstPage : AuthoringNodeReadSql.ChildrenFirstPage);

        return [.. await db.QueryAsync<AuthoringNodeReadSql.NodePageRow>(sql, parameters, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>
    /// The breadcrumb: the ancestors of the addressed node, root first and without the node itself, or — with
    /// <c>parent_id</c> and <c>include=path</c> — the chain from the root down to <b>and including</b> that parent, so a
    /// client that lists a group's children can render "root / … / parent". Null when no path was asked for and when the
    /// request listed the project's roots, which have no ancestors by definition.
    /// </summary>
    private async Task<IReadOnlyList<NodePathEntry>?> PathAsync(
        Guid orgId,
        GetAuthoringTreeQuery query,
        Guid? groupParent,
        AuthoringNodeReadSql.NodePageRow? groupNode,
        bool appendGroupNode,
        CancellationToken cancellationToken)
    {
        if (!query.IncludePath || groupParent is not { } node)
        {
            return null;
        }

        var ancestors = await db.QueryAsync<AuthoringNodeReadSql.NodePathRow>(
            AuthoringNodeReadSql.Ancestors,
            new { org_id = orgId, project_id = query.ProjectId, id = node },
            cancellationToken).ConfigureAwait(false);

        var path = ancestors.Select(row => new NodePathEntry(row.Id, row.Kind, row.Title)).ToList();

        if (appendGroupNode && groupNode is not null)
        {
            // The parent_id form: the group's own node closes the breadcrumb of its children.
            path.Add(new NodePathEntry(groupNode.Id, groupNode.Kind, groupNode.Title));
        }

        return path;
    }

    private static NodeDto ToDto(AuthoringNodeReadSql.NodePageRow row, Dictionary<Guid, AuthoringNodeReadSql.NodeCountRow> counts) =>
        counts.TryGetValue(row.Id, out var found)
            ? new NodeDto(row.Id, row.ParentId, row.Kind, row.Title, row.Position, row.Version, found.ChildCount, found.ItemCount)
            : new NodeDto(row.Id, row.ParentId, row.Kind, row.Title, row.Position, row.Version, 0, 0);
}

/// <summary>
/// The opaque cursor of the tree page (00 §9.1): the <c>(position, id)</c> keyset pair of the page's last row, encoded
/// <c>&lt;position as 8 hex digits&gt;-&lt;uuid&gt;</c>. <c>KeysetCursor</c> is the <c>(created_at, id)</c> codec of the
/// older lists; a node page orders by <c>position</c>, which is an integer, so it needs its own codec rather than a
/// timestamp cast that would lose the <c>(position, id)</c> comparison the index serves.
/// </summary>
internal static class AuthoringNodeCursor
{
    /// <summary>Encodes one row's keyset position.</summary>
    /// <param name="position">The row's sibling position (never negative).</param>
    /// <param name="id">The row's id, the tiebreaker of the total order.</param>
    internal static string Encode(int position, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{position:x8}-{id:D}");

    /// <summary>
    /// Decodes a cursor. False for anything this codec did not produce, so the caller answers 422 <c>/cursor</c>
    /// <c>malformed</c> instead of guessing a page position (the <c>KeysetCursor.TryDecode</c> rule).
    /// </summary>
    /// <param name="cursor">The raw query value.</param>
    /// <param name="position">The decoded position on success.</param>
    /// <param name="id">The decoded id on success.</param>
    internal static bool TryDecode(string? cursor, out int position, out Guid id)
    {
        position = 0;
        id = Guid.Empty;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf('-', StringComparison.Ordinal);
        if (separator != 8
            || !int.TryParse(cursor.AsSpan(0, separator), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out position)
            || position < 0
            || !Guid.TryParse(cursor.AsSpan(separator + 1), out id))
        {
            return false;
        }

        return true;
    }
}
