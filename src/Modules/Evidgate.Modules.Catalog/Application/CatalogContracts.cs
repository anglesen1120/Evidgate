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
}
