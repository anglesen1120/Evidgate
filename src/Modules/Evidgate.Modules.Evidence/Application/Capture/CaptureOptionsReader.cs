using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Application.Capture;

/// <summary>
/// <see cref="ICaptureOptionsReader"/> over the caller's tenant transaction. It never opens a connection or a transaction
/// and never commits: the read sees the caller's uncommitted rows and its rollback undoes nothing here because nothing is
/// written. Another tenant's project answers no row, so the caller gets the defaults and never its rules.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
internal sealed class CaptureOptionsReader(ITenantDb db) : ICaptureOptionsReader
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CaptureOptionsKind>> GetAsync(
        Guid orgId,
        Guid projectId,
        CancellationToken cancellationToken) =>
        CaptureOptionsRead.LoadAsync(db, orgId, projectId, cancellationToken);
}
