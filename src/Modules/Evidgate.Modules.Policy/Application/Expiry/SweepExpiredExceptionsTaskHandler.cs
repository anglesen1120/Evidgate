using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Policy.Application.Expiry;

/// <summary>
/// Platform task: lists the orgs with a due waiver or quarantine entry through the sweep role's definer, then moves
/// each org's due rows to <c>expired</c> inside that org's own tenant scope, with one audit row per row moved in the
/// same transaction.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiresTenant"/> is <c>false</c> because the scan spans tenants. The sweep function refuses any org but
/// the scope's, so a bad org id from the scan can never move another tenant's rows.
/// </para>
/// <para>
/// The state change and its audit rows commit together or not at all: a crash between them leaves the rows approved or
/// active and due, and the next hourly fire moves them, so an expiry is never recorded without its audit row nor the
/// reverse. A re-run finds nothing due and writes nothing.
/// </para>
/// <para>
/// A tenant that fails is collected and the scan goes on, so one broken org does not starve the ones behind it; the
/// fire then throws one aggregate and the queue retries it. Nothing here moves a row back: expiry is one way.
/// </para>
/// </remarks>
internal sealed partial class SweepExpiredExceptionsTaskHandler(
    ExceptionExpiryStore store,
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ILogger<SweepExpiredExceptionsTaskHandler> logger) : ITaskHandler<SweepExpiredExceptionsCommand>
{
    private static readonly AuditActor SystemActor = new(AuditActorTypes.System, "system");

    /// <inheritdoc />
    public string TaskType => ExpirySweeps.TaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        SweepExpiredExceptionsCommand payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var expired = 0;
        List<Guid>? failedOrgIds = null;
        List<Exception>? failures = null;
        Guid? after = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orgs = await store.ListDueOrgsAsync(after, ExpirySweeps.OrgBatch, cancellationToken).ConfigureAwait(false);
            if (orgs.Count == 0)
            {
                break;
            }

            foreach (var orgId in orgs)
            {
                try
                {
                    expired += await ExpireOrgAsync(orgId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    (failedOrgIds ??= []).Add(orgId);
                    (failures ??= []).Add(exception);
                    LogOrgFailed(orgId, exception);
                }
            }

            after = orgs[^1];
        }

        if (failures is not null)
        {
            throw new AggregateException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"exception expiry failed for {failures.Count} org(s): {string.Join(", ", failedOrgIds!)}"),
                failures);
        }

        LogExpired(expired);
    }

    /// <summary>One org: batches of due rows, each batch its own transaction so a backlog never holds one open.</summary>
    private async Task<int> ExpireOrgAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var moved = await ExpireBatchAsync(orgId, cancellationToken).ConfigureAwait(false);
            total += moved;
            if (moved < ExpirySweeps.RowBatch)
            {
                return total;
            }
        }
    }

    private async Task<int> ExpireBatchAsync(Guid orgId, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(SweepExpiredExceptionsCommand));
        await using var orgScope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        var moved = await store.MoveDueAsync(orgId, ExpirySweeps.RowBatch, cancellationToken).ConfigureAwait(false);
        if (moved.Count == 0)
        {
            return 0;
        }

        using var unit = audit.BeginUnit();
        foreach (var row in moved)
        {
            var waiver = row.Kind == ExceptionExpiryStore.WaiverKind;
            audit.Describe(new AuditObject(waiver ? "waiver" : "quarantine", row.Id.ToString("D")));
            audit.Record(waiver ? "waiver.expire" : "quarantine.expire", SystemActor);
        }

        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await orgScope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return moved.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Rows} waiver or quarantine row(s) past their expiry")]
    private partial void LogExpired(int rows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception expiry failed for org {Org}")]
    private partial void LogOrgFailed(Guid org, Exception exception);
}
