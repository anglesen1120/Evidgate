using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// One keyset page of a project's manual test cases (route 12).
/// </summary>
/// <param name="ProjectId">The project from the route; a project of another tenant is a 404.</param>
/// <param name="NodeId">Optional filter: the cases whose item sits under this node (an exact node, not its subtree).</param>
/// <param name="ItemId">Optional filter: the cases of this checklist item.</param>
/// <param name="State">Optional filter: <c>draft</c>, <c>approved</c> or <c>retired</c>.</param>
/// <param name="Priority">Optional filter: <c>critical</c>, <c>high</c>, <c>medium</c> or <c>low</c>.</param>
/// <param name="Covered">
/// Optional filter: <c>true</c> keeps the cases linked to at least one live catalog case, <c>false</c> the cases with no
/// such link. This is authoring coverage, not the execution coverage of another module's report.
/// </param>
/// <param name="Q">Optional case-insensitive title substring, at most 200 code points.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
internal sealed record ListManualCasesQuery(
    Guid ProjectId,
    Guid? NodeId,
    Guid? ItemId,
    string? State,
    string? Priority,
    bool? Covered,
    string? Q,
    string? Cursor,
    int Limit) : IQuery<Result<ManualCaseListResponse>>;

/// <summary>
/// Handler of <see cref="ListManualCasesQuery"/>: four statements (the project probe, the project's linked catalog
/// ids, the catalog's archived subset among them and one page) in the read-only tenant transaction, the plan's
/// <c>(code, id)</c> order, and a 422 per unsupported filter value.
/// </summary>
/// <remarks>
/// The <c>q</c> filter is <c>position(lower(@q) in lower(title)) &gt; 0</c> rather than <c>ilike '%…%'</c>: a substring
/// match has no metacharacters, so there is nothing to escape and no wildcard injection to defend against — the
/// feature plan asks for exactly that outcome. Both sides are lowered by the database's <c>C.UTF-8</c> ctype, which folds
/// ASCII and keeps Vietnamese diacritics distinct — the same rule the catalog's title search uses. The filter is not
/// index-served (a substring cannot be), which the feature plan accepts for the title search on a project-bounded table.
/// </remarks>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="catalog">The catalog surface, for which of the project's linked cases are archived.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListManualCasesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ICatalogContracts catalog) : IQueryHandler<ListManualCasesQuery, Result<ManualCaseListResponse>>
{
    /// <summary>The longest accepted <c>q</c>, the plan's bound.</summary>
    internal const int MaximumQueryLength = 200;

    /// <inheritdoc />
    public async ValueTask<Result<ManualCaseListResponse>> Handle(
        ListManualCasesQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListManualCasesQuery));

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
                "state is one of draft, approved or retired",
                [new ErrorDetail("/state", "value_invalid")]));
        }

        if (query.Priority is { } priority && !AuthoringPriorities.IsValid(priority))
        {
            return Result.Failure<ManualCaseListResponse>(Error.Invalid(
                "priority is one of critical, high, medium or low",
                [new ErrorDetail("/priority", "value_invalid")]));
        }

        var text = AuthoringEndpointText.Blank(query.Q);

        if (text is { } raw)
        {
            if (AuthoringText.ContainsNul(raw))
            {
                // `q` reaches a `text` comparison, which refuses U+0000 (22021) after the statement has started: the
                // caller's NUL is answered here instead, in the same 422 shape as any other unusable q (security S-1/S-2).
                return Result.Failure<ManualCaseListResponse>(Error.Invalid(
                    "q must not contain a NUL (U+0000) character",
                    [new ErrorDetail("/q", "invalid_text")]));
            }

            // The stored titles are NFC, so a decomposed query would never match one: compose it first, then
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

        // Only archived cases have to be excluded for a link to count as live, and the list projection may not read
        // `test_case` itself (module boundaries): the project's distinct linked ids go to the catalog, which answers
        // the archived subset. Both reads precede the page, so a link written in between still reads as live — one
        // request is eventually consistent against the catalog, never torn.
        var linked = await db.QueryAsync<Guid>(
            AuthoringManualCaseReadSql.LinkedCaseIds,
            new { org_id = orgId, project_id = query.ProjectId },
            cancellationToken).ConfigureAwait(false);

        var archived = await catalog.GetArchivedCaseIdsAsync(orgId, query.ProjectId, linked, cancellationToken)
            .ConfigureAwait(false);

        var parameters = new
        {
            org_id = orgId,
            project_id = query.ProjectId,
            item_id = query.ItemId,
            node_id = query.NodeId,
            state = query.State,
            priority = query.Priority,
            covered = query.Covered,
            archived_case_ids = archived.ToArray(),
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

/// <summary>One manual test case with its steps (route 14).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
internal sealed record GetManualCaseQuery(Guid ProjectId, Guid CaseId) : IQuery<Result<ManualCaseDetailDto>>;

/// <summary>
/// Handler of <see cref="GetManualCaseQuery"/>: one statement, no separate project probe — the filter carries the
/// project, so a case of another project or another tenant is one 404 the tenant policy decides.
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
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetManualCaseQuery));

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
    /// <summary>The separator between the two members; a code cannot contain it (see <c>AuthoringCode.FormatPattern</c>).</summary>
    private const char Separator = '\n';

    /// <summary>Encodes the last row of a page as the next page's cursor.</summary>
    /// <param name="code">That row's code.</param>
    /// <param name="id">That row's id.</param>
    internal static string Encode(string code, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(code + Separator + id.ToString("D")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// Decodes a cursor, or answers false for anything this class did not encode. The code half is re-checked against
    /// <c>AuthoringCode.FormatPattern</c> rather than trusted: a tampered cursor would otherwise send arbitrary
    /// text into the keyset comparison of a <c>text</c> column, where a NUL or an unpaired surrogate is a 22021 from the
    /// database (a 500) instead of the 422 this route answers for a cursor it never issued (security S-1/S-2).
    /// </summary>
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

        if (separator <= 0)
        {
            return false;
        }

        var code = decoded[..separator];

        if (!AuthoringCode.TryParse(code, out _)
            || !Guid.TryParse(decoded[(separator + 1)..], out var id)
            || id == Guid.Empty)
        {
            return false;
        }

        value = (code, id);

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

    /// <summary>
    /// An optional boolean filter: absent → null; present but empty, or anything but <c>true</c>/<c>false</c>, is the
    /// 422 naming it — a value the caller sent is malformed, not the same request as no value.
    /// </summary>
    /// <param name="query">The request query string.</param>
    /// <param name="name">The query key, e.g. <c>covered</c>.</param>
    /// <param name="value">The parsed flag, or null.</param>
    internal static Error? OptionalBoolean(IQueryCollection query, string name, out bool? value)
    {
        ArgumentNullException.ThrowIfNull(query);

        value = null;

        if (!query.TryGetValue(name, out var rawValues))
        {
            return null;
        }

        var raw = Blank(rawValues.ToString());

        if (string.Equals(raw, "true", StringComparison.Ordinal))
        {
            value = true;

            return null;
        }

        if (string.Equals(raw, "false", StringComparison.Ordinal))
        {
            value = false;

            return null;
        }

        return Error.Invalid(
            string.Create(CultureInfo.InvariantCulture, $"{name} is true or false"),
            [new ErrorDetail("/" + name, "value_invalid")]);
    }
}
