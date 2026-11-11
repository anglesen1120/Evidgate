using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// The module's in-process surface (00 §5.5, 12.3.1 §5): the adapter that exposes <see cref="IUsageLedger"/> to the
/// metering callers (04.8.1's accepted batch, 06.5.2's period, 12.2.1's support hours). Catalog's
/// <c>CatalogContracts</c> and Policy's <c>PolicyContracts</c> are the precedent — a thin, transaction-neutral wrapper that
/// opens no transaction of its own and runs on the caller's scope, so a refused or rolled-back caller leaves no ledger row
/// and the emitted row commits with the work it meters.
/// <para>
/// It deliberately does not dispatch the <see cref="Evidgate.Modules.Usage.Application.Commands.RecordUsageBatch"/> command: the in-process caller
/// holds no <c>billing:write</c> (an ingest producer holds <c>event:write</c>), and the billing keys already make the write
/// idempotent (ruling T43), so a second idempotency record would only add a statement. The validation rules are the same
/// ones the command applies; a violation here is the calling module's bug and throws instead of answering a Result.
/// </para>
/// </summary>
internal sealed class UsageContracts(
    UsageLedgerStore store,
    LedgerRedateAudit redateAudit,
    ITenantScopeAccessor scopes) : IUsageLedger
{
    /// <inheritdoc />
    /// <remarks>
    /// A period an issued statement froze no longer refuses the batch: the insert guard re-dates such a row into the
    /// next open month and this call records that move in the caller's own transaction, so an emitter that meters
    /// after month-end bills the work late instead of failing. A month with no open period after it is the one refusal
    /// that still propagates, because there is nowhere to bill.
    /// </remarks>
    public async Task<UsageBatchResult> RecordAsync(
        RecordUsageBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var violations = UsageBatchRules.Validate(request.Entries);
        if (violations.Count > 0)
        {
            var first = violations[0];

            throw new ArgumentException(
                $"the usage batch is invalid: {first.Path} is {first.Reason}",
                nameof(request));
        }

        if (request.Entries.Count == 0)
        {
            return new UsageBatchResult([], [], []);
        }

        var batch = UsageBatchRules.Prepare(request.Entries);
        var orgId = scopes.Require().OrgId;

        // The projects are checked in one read inside the caller's transaction: a foreign-key error would abort that
        // transaction, and the caller is mid-work — a refused entry it can fix beats a broken unit of work.
        var projectViolations = await store.ValidateProjectsAsync(orgId, request.Entries, cancellationToken)
            .ConfigureAwait(false);
        if (projectViolations.Count > 0)
        {
            var first = projectViolations[0];
            throw new ArgumentException(
                $"the usage batch is invalid: {first.Path} is {first.Reason}",
                nameof(request));
        }

        var write = await store
            .RecordBatchAsync(orgId, batch, cancellationToken)
            .ConfigureAwait(false);

        // The store rolled the whole batch back on a conflict, so there is nothing to audit, meter or merge: the
        // conflicting keys are the answer and the write is permanent, not a retryable failure.
        if (write.Conflicts.Count > 0)
        {
            return new UsageBatchResult([], [], write.Conflicts);
        }

        await redateAudit.RecordAsync(orgId, write.Redated, cancellationToken).ConfigureAwait(false);

        UsageMetering.Schedule(scopes.Require(), batch.Rows, write.Inserted);

        return UsageBatchOutcome.Merge(batch, write.Inserted);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The org is the scope's tenant, so the unique index answers one read and row security turns another org's key into
    /// an absence. No validation runs: the key is the caller's own, and reading it changes nothing.
    /// </remarks>
    public Task<UsageEntryRecord?> FindAsync(string billingKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(billingKey);

        return store.FindAsync(scopes.Require().OrgId, billingKey, cancellationToken);
    }
}
