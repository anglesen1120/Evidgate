using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
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
}
