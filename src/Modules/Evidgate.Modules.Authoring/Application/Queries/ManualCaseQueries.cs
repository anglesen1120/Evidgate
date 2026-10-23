using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// One keyset page of a project's manual test cases (route 12, 13.1.1 §5).
/// </summary>
/// <param name="ProjectId">The project from the route; a project of another tenant is a 404 (AC10).</param>
/// <param name="NodeId">Optional filter: the cases whose item sits under this node (an exact node, not its subtree).</param>
/// <param name="ItemId">Optional filter: the cases of this checklist item.</param>
/// <param name="State">Optional filter: <c>draft</c>, <c>approved</c> or <c>retired</c>.</param>
/// <param name="Priority">Optional filter: <c>critical</c>, <c>high</c>, <c>medium</c> or <c>low</c>.</param>
/// <param name="Q">Optional case-insensitive title substring, at most 200 code points.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
internal sealed record ListManualCasesQuery(
    Guid ProjectId,
    Guid? NodeId,
    Guid? ItemId,
    string? State,
    string? Priority,
    string? Q,
    string? Cursor,
    int Limit) : IQuery<Result<ManualCaseListResponse>>;

/// <summary>
/// Handler of <see cref="ListManualCasesQuery"/>: two statements (the project probe and one page) in the read-only
/// tenant transaction, the plan's <c>(code, id)</c> order, and a 422 per unsupported filter value.
/// </summary>
/// <remarks>
/// The <c>q</c> filter is <c>position(lower(@q) in lower(title)) &gt; 0</c> rather than <c>ilike '%…%'</c>: a substring
/// match has no metacharacters, so there is nothing to escape and no wildcard injection to defend against (the plan's
/// §5 note asks for exactly that outcome). Both sides are lowered by the database's <c>C.UTF-8</c> ctype, which folds
/// ASCII and keeps Vietnamese diacritics distinct — the same rule the catalog's title search uses. The filter is not
/// index-served (a substring cannot be), which §4 P4 accepts for the title search on a project-bounded table.
/// </remarks>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListManualCasesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<ListManualCasesQuery, Result<ManualCaseListResponse>>
{
    /// <summary>The longest accepted <c>q</c> (the plan's §5 bound).</summary>
    internal const int MaximumQueryLength = 200;

    /// <inheritdoc />
    public async ValueTask<Result<ManualCaseListResponse>> Handle(
        ListManualCasesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The project-scope gate first (AuthoringRules): RLS scopes rows per org, so a project-scoped credential
        // addressing a sibling project of the same tenant must be refused here, before any read.
        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<ManualCaseListResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (query.State is { } state && !ManualTestCaseStateMachine.States.Contains(state, StringComparer.Ordinal))
        {
            return Result.Failure<ManualCaseListResponse>(Error.Invalid(
                "state is one of draft, approved or retired (13.1.1 §6)",
                [new ErrorDetail("/state", "value_invalid")]));
        }

        if (query.Priority is { } priority && !AuthoringPriorities.IsValid(priority))
        {
            return Result.Failure<ManualCaseListResponse>(Error.Invalid(
                "priority is one of critical, high, medium or low (13.1.1 §3)",
                [new ErrorDetail("/priority", "value_invalid")]));
        }

        var text = AuthoringEndpointText.Blank(query.Q);

        if (text is { } raw)
        {
            // The stored titles are NFC (AC14), so a decomposed query would never match one: compose it first, then
            // count code points (the unit the database's char_length and the schema's maxLength count).
            text = AuthoringText.Normalize(raw);

            if (!AuthoringText.Fits(text, MaximumQueryLength))
            {
                return Result.Failure<ManualCaseListResponse>(Error.Invalid(
                    string.Create(CultureInfo.InvariantCulture, $"q is at most {MaximumQueryLength} characters"),
                    [new ErrorDetail("/q", "too_long")]));
            }
        }

        (string Code, Guid Id)? cursor = null;

        if (AuthoringEndpointText.Blank(query.Cursor) is { } rawCursor)
        {
            if (!ManualCaseCursor.TryDecode(rawCursor, out var decoded))
            {
                return Result.Failure<ManualCaseListResponse>(Error.Invalid(
                    "cursor is the opaque next_cursor of the previous page",
                    [new ErrorDetail("/cursor", "malformed")]));
            }

            cursor = decoded;
        }

        if (await db.ScalarAsync<int?>(AuthoringManualCaseReadSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Failure<ManualCaseListResponse>(AuthoringRules.ProjectNotFound);
        }

        var parameters = new
        {
            org_id = orgId,
            project_id = query.ProjectId,
            item_id = query.ItemId,
            node_id = query.NodeId,
            state = query.State,
            priority = query.Priority,
            q = text,
            cursor_code = cursor?.Code,
            cursor_id = cursor?.Id,
            take = query.Limit + 1,
        };

        var rows = await db.QueryAsync<ManualCaseListRow>(
            cursor is null ? AuthoringManualCaseReadSql.CasesFirstPage : AuthoringManualCaseReadSql.CasesNextPage,
            parameters,
            cancellationToken).ConfigureAwait(false);

        var items = rows.Count > query.Limit ? rows.Take(query.Limit).ToArray() : rows.ToArray();
        var next = rows.Count > query.Limit && items.Length > 0
            ? ManualCaseCursor.Encode(items[^1].Code, items[^1].CaseId)
            : null;

        return Result.Success(new ManualCaseListResponse([.. items.Select(row => row.ToDto())], next));
    }
}

/// <summary>One manual test case with its steps (route 14, 13.1.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
internal sealed record GetManualCaseQuery(Guid ProjectId, Guid CaseId) : IQuery<Result<ManualCaseDetailDto>>;

/// <summary>
/// Handler of <see cref="GetManualCaseQuery"/>: one statement, no separate project probe — the filter carries the
/// project, so a case of another project or another tenant is one 404 the tenant policy decides (AC10).
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetManualCaseQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetManualCaseQuery, Result<ManualCaseDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ManualCaseDetailDto>> Handle(
        GetManualCaseQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<ManualCaseDetailDto>(scopeError);
        }

        var orgId = scopes.Require().OrgId;
        var rows = await db.QueryAsync<ManualCaseRow>(
            AuthoringManualCaseReadSql.CaseDetail,
            new { org_id = orgId, project_id = query.ProjectId, case_id = query.CaseId },
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0
            ? Result.Failure<ManualCaseDetailDto>(Error.NotFound("no manual test case with that id is visible to this tenant"))
            : Result.Success(rows[0].ToDto());
    }
}

/// <summary>
/// The opaque <c>cursor</c> of route 12: the <c>(code, id)</c> pair the keyset seeks past, Base64URL-encoded so a
/// client cannot read it as a code and cannot be tempted to build one.
/// </summary>
internal static class ManualCaseCursor
{
    /// <summary>The separator between the two members; a code cannot contain it (<c>^[A-Z][A-Z0-9]{1,7}-[0-9]{3,10}$</c>).</summary>
    private const char Separator = '\n';

    /// <summary>Encodes the last row of a page as the next page's cursor.</summary>
    /// <param name="code">That row's code.</param>
    /// <param name="id">That row's id.</param>
    internal static string Encode(string code, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(code + Separator + id.ToString("D")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Decodes a cursor, or answers false for anything this class did not encode.</summary>
    /// <param name="cursor">The raw query value.</param>
    /// <param name="value">The decoded pair on success.</param>
    internal static bool TryDecode(string cursor, out (string Code, Guid Id) value)
    {
        value = default;

        var text = cursor.Replace('-', '+').Replace('_', '/');
        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var separator = decoded.IndexOf(Separator, StringComparison.Ordinal);

        if (separator <= 0 || !Guid.TryParse(decoded[(separator + 1)..], out var id) || id == Guid.Empty)
        {
            return false;
        }

        value = (decoded[..separator], id);

        return true;
    }
}

/// <summary>
/// The two query-string readers of this surface that <c>AuthoringEndpointSupport</c> does not offer: a blank value
/// reads as absent (the catalog list routes' rule), and an optional uuid filter is a 422 rather than a silent
/// unfiltered page.
/// </summary>
internal static class AuthoringEndpointText
{
    /// <summary>An absent or empty value reads as <c>null</c>.</summary>
    /// <param name="value">The raw query value.</param>
    internal static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>An optional uuid filter: absent → null, unparseable → the 422 naming the member.</summary>
    /// <param name="query">The request query string.</param>
    /// <param name="name">The query key, e.g. <c>node_id</c>.</param>
    /// <param name="id">The parsed id, or null.</param>
    internal static Error? OptionalId(IQueryCollection query, string name, out Guid? id)
    {
        ArgumentNullException.ThrowIfNull(query);

        id = null;

        var raw = Blank(query[name].ToString());

        if (raw is null)
        {
            return null;
        }

        if (!Guid.TryParse(raw, out var parsed) || parsed == Guid.Empty)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"{name} must be a uuid"),
                [new ErrorDetail("/" + name, "value_invalid")]);
        }

        id = parsed;

        return null;
    }
}
