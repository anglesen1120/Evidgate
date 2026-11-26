using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Healing.Application;

/// <summary>One incident as the list and the detail read answer it: no attempt payload and no error text.</summary>
/// <param name="Id">The incident id.</param>
/// <param name="ProjectId">The project of the failing run.</param>
/// <param name="RunId">The run the failure belongs to.</param>
/// <param name="AttemptId">The first failing attempt.</param>
/// <param name="CaseId">The failing test case.</param>
/// <param name="CaseRevisionId">The case revision that ran.</param>
/// <param name="ExecutionKey">The planned execution's key (64 lowercase hex).</param>
/// <param name="Engine">The framework that ran the case.</param>
/// <param name="State"><c>open</c>, <c>triaged</c> or <c>closed</c>.</param>
/// <param name="ContextState"><c>pending</c>, <c>complete</c> or <c>partial</c>.</param>
/// <param name="AttemptCount">The failing attempt plus its recorded retries.</param>
/// <param name="CreatedAt">When the incident was recorded.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Version">The row version.</param>
internal sealed record Incident(
    Guid Id,
    Guid ProjectId,
    Guid RunId,
    Guid AttemptId,
    Guid CaseId,
    Guid CaseRevisionId,
    string ExecutionKey,
    string Engine,
    string State,
    string ContextState,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>A cursor page of incidents, newest first.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">Opaque position of the next page, or null on the last one.</param>
internal sealed record IncidentPage(IReadOnlyList<Incident> Items, string? NextCursor);

/// <summary>The filters and bounds of one incident list read; shared by the endpoint binding and the handler.</summary>
internal static class IncidentListRules
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    internal static readonly string[] States = ["open", "triaged", "closed"];

    /// <summary>Null when <paramref name="state"/> and <paramref name="executionKey"/> are absent or storable values.</summary>
    internal static Error? Validate(string? state, string? executionKey)
    {
        if (state is not null && !States.Contains(state, StringComparer.Ordinal))
        {
            return Invalid("state");
        }

        // 64 lowercase hex, the table's own check; the length test also keeps a long value out of the statement.
        return executionKey is not null && !(executionKey.Length == 64 && executionKey.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            ? Invalid("execution_key")
            : null;
    }

    /// <summary>Reads <c>?limit=</c>: absent is the default, anything outside 1..200 is 422.</summary>
    internal static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;
        return null;
    }

    private static Error Invalid(string name) =>
        Error.Invalid($"{name} has an invalid value", [new ErrorDetail(name, "invalid_value")]);
}

/// <summary>
/// The Dapper statements over <c>public.incident_list</c>. Both are one statement: RLS hides other orgs and the
/// explicit <c>org_id</c> predicate is the access path of <c>incident_project_list</c>. The order is that index's:
/// newest first, <c>id</c> ascending for ties, so the next page continues at an equal timestamp by a larger id.
/// </summary>
internal static class IncidentSql
{
    private const string Columns = """
        id, project_id, run_id, attempt_id, case_id, case_revision_id, execution_key, engine, state, context_state,
        attempt_count, created_at, updated_at, version
        """;

    private const string Filters = """
           and (@state::text is null or state = @state::text)
           and (@execution_key::text is null or execution_key = @execution_key::text)
        """;

    internal const string FirstPage = "select " + Columns + """

          from public.incident_list
         where org_id = @org_id::uuid and project_id = @project_id::uuid
        """ + Filters + """

         order by created_at desc, id
         limit @take
        """;

    internal const string NextPage = "select " + Columns + """

          from public.incident_list
         where org_id = @org_id::uuid and project_id = @project_id::uuid
        """ + Filters + """

           and created_at <= @cursor_created_at::timestamptz
           and (created_at < @cursor_created_at::timestamptz or id > @cursor_id::uuid)
         order by created_at desc, id
         limit @take
        """;

    internal const string ById = "select " + Columns + """

          from public.incident_list
         where org_id = @org_id::uuid and id = @id::uuid
        """;
}

/// <summary>The module's read path over <c>incident_list</c>, inside the caller's read-only tenant transaction.</summary>
internal sealed class IncidentReadStore(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>One keyset page: <paramref name="take"/> rows (the caller asks for one more than it returns).</summary>
    internal Task<IReadOnlyList<Incident>> ListAsync(
        Guid projectId,
        string? state,
        string? executionKey,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        int take,
        CancellationToken cancellationToken) =>
        db.QueryAsync<Incident>(
            after is null ? IncidentSql.FirstPage : IncidentSql.NextPage,
            new
            {
                org_id = scopes.Require().OrgId,
                project_id = projectId,
                state,
                execution_key = executionKey,
                cursor_created_at = after?.CreatedAt,
                cursor_id = after?.Id,
                take,
            },
            cancellationToken);

    /// <summary>The incident, or null when no row of this tenant has the id.</summary>
    internal Task<Incident?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<Incident>(
            IncidentSql.ById,
            new { org_id = scopes.Require().OrgId, id },
            cancellationToken);
}
