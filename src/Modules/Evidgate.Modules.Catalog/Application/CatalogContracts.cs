using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;

namespace Evidgate.Modules.Catalog.Application;

/// <summary><see cref="ICatalogContracts"/> over the caller's tenant transaction (03.2.1 §6, 00 §8.10).</summary>
internal sealed class CatalogContracts(ITenantDb db) : ICatalogContracts
{
    /// <inheritdoc />
    public async ValueTask<bool> PublishSuiteRevisionAsync(Guid suiteRevisionId, CancellationToken cancellationToken)
    {
        // draft -> published is the only edit the guard trigger allows; a published revision stays as it is.
        await db.ExecuteAsync(SuiteReadSql.Publish, new { revision_id = suiteRevisionId }, cancellationToken).ConfigureAwait(false);

        return await db.ScalarAsync<int?>(SuiteReadSql.RevisionExists, new { revision_id = suiteRevisionId }, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetDataIsolationAsync(Guid suiteRevisionId, CancellationToken cancellationToken) =>
        await db.ScalarAsync<string>(SuiteReadSql.DataIsolation, new { revision_id = suiteRevisionId }, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> SuiteRevisionExistsAsync(Guid projectId, Guid suiteRevisionId, CancellationToken cancellationToken) =>
        await db.ScalarAsync<int?>(
            """
            select 1 from public.suite_revision r
              join public.suite s on s.org_id = r.org_id and s.id = r.suite_id
             where r.id = @revision_id and s.project_id = @project_id
            """,
            new { revision_id = suiteRevisionId, project_id = projectId },
            cancellationToken).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async ValueTask<IReadOnlySet<Guid>> FindCaseRevisionsAsync(Guid projectId, IReadOnlyCollection<Guid> caseRevisionIds, CancellationToken cancellationToken)
    {
        if (caseRevisionIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // The ids travel as one JSON text (the Dapper read path refuses array parameters).
        var json = System.Text.Json.JsonSerializer.Serialize(caseRevisionIds);
        var found = await db.QueryAsync<Guid>(
            """
            select r.id from public.test_revision r
              join public.test_case c on c.org_id = r.org_id and c.id = r.case_id
             where c.project_id = @project_id
               and r.id in (select value::uuid from jsonb_array_elements_text(@ids::jsonb))
            """,
            new { project_id = projectId, ids = json },
            cancellationToken).ConfigureAwait(false);

        return found.ToHashSet();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<Guid, Guid>> GetCaseIdsAsync(Guid projectId, IReadOnlyCollection<Guid> caseRevisionIds, CancellationToken cancellationToken)
    {
        if (caseRevisionIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        // Same scoping and same one-statement JSON-text transport as FindCaseRevisionsAsync (the Dapper read path refuses
        // array parameters); the manifest caps the plan at 10000 executions, so the id set stays bounded.
        var json = System.Text.Json.JsonSerializer.Serialize(caseRevisionIds);
        var rows = await db.QueryAsync<CaseRevisionRow>(
            """
            select r.id as case_revision_id, c.id as case_id from public.test_revision r
              join public.test_case c on c.org_id = r.org_id and c.id = r.case_id
             where c.project_id = @project_id
               and r.id in (select value::uuid from jsonb_array_elements_text(@ids::jsonb))
            """,
            new { project_id = projectId, ids = json },
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(row => row.CaseRevisionId, row => row.CaseId);
    }

    /// <inheritdoc />
    public IReadOnlyList<string?> NormalizeVariantHashes(IReadOnlyList<System.Text.Json.JsonElement> variantParams)
    {
        ArgumentNullException.ThrowIfNull(variantParams);

        var hashes = new string?[variantParams.Count];
        // A 10000-row plan repeats a handful of parameter sets: normalise each distinct text once.
        var memo = new Dictionary<string, string?>(StringComparer.Ordinal);
        var problems = new List<VariantProblem>();
        for (var index = 0; index < variantParams.Count; index++)
        {
            var element = variantParams[index];
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                // Not a variant object (null, array, scalar): no hash, no refusal to report.
                hashes[index] = null;
                continue;
            }

            var raw = element.GetRawText();
            if (!memo.TryGetValue(raw, out var hash))
            {
                problems.Clear();
                hash = VariantNormalizer.Normalize(element, "/params", problems)?.Hash;
                memo[raw] = hash;
            }

            hashes[index] = hash;
        }

        return hashes;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DependantMember>> GetDependantsAsync(Guid suiteRevisionId, Guid caseRevisionId, string variantHash, CancellationToken cancellationToken)
    {
        // One recursive statement however deep the graph (a UNION walk that terminates over a cycle); RLS scopes it to the
        // tenant, so another tenant's revision or member simply yields no rows. The Dapper read path refuses array
        // parameters, and none is needed: the root is one (case_revision_id, variant_hash) pair.
        var rows = await db.QueryAsync<DependantMember>(
            SuiteReadSql.Dependants,
            new { suite_revision_id = suiteRevisionId, case_revision_id = caseRevisionId, variant_hash = variantHash },
            cancellationToken).ConfigureAwait(false);

        return [.. rows];
    }

    /// <inheritdoc />
    public async ValueTask<EvaluationSuite?> GetEvaluationSuiteAsync(Guid orgId, Guid suiteRevisionId, CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<EvaluationSuite>(
            EvaluationReadSql.LoadSuite,
            new { org_id = orgId, suite_revision_id = suiteRevisionId },
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<Guid, EvaluationCaseIdentity>> GetEvaluationCasesAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> caseRevisionIds,
        CancellationToken cancellationToken)
    {
        if (caseRevisionIds.Count == 0)
        {
            return new Dictionary<Guid, EvaluationCaseIdentity>();
        }

        var json = System.Text.Json.JsonSerializer.Serialize(caseRevisionIds);
        var rows = await db.QueryAsync<CaseIdentityRow>(
            EvaluationReadSql.LoadCases,
            new { org_id = orgId, ids = json },
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(
            row => row.CaseRevisionId,
            row => new EvaluationCaseIdentity(row.CaseId, row.CaseKey));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Guid>> GetArchivedCaseIdsAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyCollection<Guid> caseIds,
        CancellationToken cancellationToken)
    {
        if (caseIds.Count == 0)
        {
            return [];
        }

        return await db.QueryAsync<Guid>(
            CatalogReadSql.ArchivedCaseIds,
            new { org_id = orgId, project_id = projectId, ids = caseIds.ToArray() },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<Guid, string>> GetCaseStatusesAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyCollection<Guid> caseIds,
        CancellationToken cancellationToken)
    {
        if (caseIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var json = System.Text.Json.JsonSerializer.Serialize(caseIds);
        var rows = await db.QueryAsync<CaseStatusRow>(
            CatalogReadSql.CaseStatuses,
            new { org_id = orgId, project_id = projectId, ids = json },
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(row => row.Id, row => row.Status);
    }

    private sealed record CaseStatusRow(Guid Id, string Status);

    private sealed record CaseRevisionRow(Guid CaseRevisionId, Guid CaseId);

    private sealed record CaseIdentityRow(Guid CaseRevisionId, Guid CaseId, string CaseKey);
}
