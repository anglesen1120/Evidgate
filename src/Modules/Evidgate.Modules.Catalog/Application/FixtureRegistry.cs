using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;

namespace Evidgate.Modules.Catalog.Application;

/// <summary><see cref="IFixtureRegistry"/> over the caller's tenant transaction (03.3.1, 00 §8.10).</summary>
internal sealed class FixtureRegistry(ITenantDb db) : IFixtureRegistry
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlySet<FixtureRef>> ExistsAsync(
        Guid projectId,
        IReadOnlyCollection<FixtureRef> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return new HashSet<FixtureRef>();
        }

        // The pairs travel as one JSON text: the Dapper read path refuses array parameters, and one recordset keeps the
        // whole check to a single statement however many references the request carries.
        var rows = await db.QueryAsync<ReferenceRow>(
            FixtureReadSql.ExistingReferences,
            new { project_id = projectId, rows = System.Text.Json.JsonSerializer.Serialize(references.Select(reference => new { name = reference.Name, revision = reference.Revision })) },
            cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new FixtureRef(row.Name, row.Revision)).ToHashSet();
    }

    private sealed class ReferenceRow
    {
        public string Name { get; set; } = string.Empty;

        public int Revision { get; set; }
    }
}
