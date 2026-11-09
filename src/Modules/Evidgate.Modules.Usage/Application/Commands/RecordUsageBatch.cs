using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Usage.Application.Commands;

/// <summary>
/// Record a batch of ledger rows (12.3.1 §5). The in-process callers reach the same write path through
/// <see cref="IUsageLedger"/>, which needs no pipeline and no key — their billing keys are the dedupe. This command is the
/// operator route's shape (12.3.1-T2.4, <c>POST /internal/operator/billing/usage</c>), which is why it carries an
/// <c>Idempotency-Key</c> (ruling T11): the key makes a retried request answer the first response verbatim, on top of the
/// per-row dedupe the billing keys already give.
/// </summary>
/// <param name="Entries">The entries to record; empty is a no-op and at most <see cref="IUsageLedger.MaxBatchSize"/> are accepted.</param>
/// <param name="IdempotencyKey">The caller's <c>Idempotency-Key</c> (00 §9.1), claimed for the operation below.</param>
internal sealed record RecordUsageBatch(
    IReadOnlyList<UsageEntryInput> Entries,
    string IdempotencyKey)
    : ICommand<Result<UsageBatchResult>>, IIdempotentCommand
{
    /// <summary>The 00 §7 operation the key is claimed for: stable across a rename.</summary>
    string IIdempotentCommand.Operation => "usage:record_batch";
}

/// <summary>
/// Handler of <see cref="RecordUsageBatch"/> (12.3.1-T1.4): validate the whole batch, then write it with one statement
/// inside the caller's tenant transaction, then answer the inserted and duplicate billing keys (plan §5).
/// <para>
/// The organization is the scope's tenant (<see cref="ITenantScopeAccessor"/>), never a value of the request: a caller
/// cannot name another org, and RLS would refuse it anyway (00 §5.4, ruling T2). A batch whose every key is already on
/// file changed nothing, so it describes no audit object and suppresses its row — the same no-op rule 13.1.1's import
/// and 04.1.1's ingest use. A batch that inserted rows audits under the first inserted row's id with the row count.
/// A row whose period is already issued is re-dated into the next open period and audited as a move; only a period
/// with no open month after it answers a conflict, and then nothing is written.
/// </para>
/// </summary>
[AuditedAction("usage.record_batch")]
[Permission(Permissions.Billing.Write)]
internal sealed class RecordUsageBatchHandler(
    UsageLedgerStore store,
    LedgerRedateAudit redateAudit,
    ITenantScopeAccessor scopes,
    IAuditTrail audit) : ICommandHandler<RecordUsageBatch, Result<UsageBatchResult>>
{
    /// <summary>
    /// The conflict code of a batch that names a period whose month and every month after it are issued: nothing about
    /// the request is malformed, there is simply no open period left to bill.
    /// </summary>
    internal const string NoOpenPeriodCode = "no_open_period";

    /// <summary>Runs the batch.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<UsageBatchResult>> Handle(RecordUsageBatch command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Usage, nameof(RecordUsageBatch));

        // (a) The whole batch is validated before any statement runs, so a refused call writes nothing and answers one
        // 00 §9.2 detail per offending entry, indexed by position (plan §5, 12.3.1-T1.4).
        var violations = UsageBatchRules.Validate(command.Entries);
        if (violations.Count > 0)
        {
            return Result.Failure<UsageBatchResult>(Error.Invalid(
                "the usage batch was rejected",
                [.. violations.Select(violation => new ErrorDetail(violation.Path, violation.Reason, violation.Index))]));
        }

        // (b) An empty batch is a well-formed no-op: no statement, no audit row, two empty lists.
        if (command.Entries.Count == 0)
        {
            audit.Suppress();
            return Result.Success(new UsageBatchResult([], []));
        }

        // (c) The tenant comes from the open scope only (00 §5.4 rule: never from the request).
        var orgId = scopes.Require().OrgId;

        // (c.1) The projects are checked in one read in this same transaction before anything is written: a foreign-key
        // violation would abort the caller's transaction, so an unknown (or other-org) project is refused per entry instead.
        var projectViolations = await store.ValidateProjectsAsync(orgId, command.Entries, cancellationToken)
            .ConfigureAwait(false);
        if (projectViolations.Count > 0)
        {
            return Result.Failure<UsageBatchResult>(Error.Invalid(
                "the usage batch was rejected",
                [.. projectViolations.Select(violation => new ErrorDetail(violation.Path, violation.Reason, violation.Index))]));
        }

        var batch = UsageBatchRules.Prepare(command.Entries);

        // (d) One statement for the batch (plan §8 P2); the returning set is the inserted half of the answer. The
        // duplicate keys of the same call are counted in this transaction, so a refusal never leaves a tally behind.
        // A row the insert guard re-dated is billed in its new month, and there is nowhere to bill one whose month and
        // every month after it are issued: that refusal is the command's conflict.
        UsageLedgerWrite write;
        try
        {
            write = await store
                .RecordBatchAsync(orgId, batch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LedgerNoOpenPeriodException refused)
        {
            // A failed Result writes no audit row: the stage records only a command that succeeds.
            return Result.Failure<UsageBatchResult>(Error.Conflict(NoOpenPeriodCode, refused.Message));
        }

        var result = UsageBatchOutcome.Merge(batch, write.Inserted);

        // (e) Audit: the rows this call wrote, or nothing at all when every key was already on file. A re-dated row is
        // audited before the batch object is described, because the last description is the one the command's own
        // audit entry carries and that entry must name the rows, not the move.
        if (result.Inserted.Count == 0)
        {
            audit.Suppress();
        }
        else
        {
            await redateAudit.RecordAsync(orgId, write.Redated, cancellationToken).ConfigureAwait(false);

            var insertedKeys = write.Inserted.ToHashSet(StringComparer.Ordinal);
            var firstInserted = batch.Rows.First(row => insertedKeys.Contains(row.BillingKey));

            audit.Describe(new AuditObject(
                "usage_ledger",
                firstInserted.Id.ToString("D"),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = result.Inserted.Count.ToString(CultureInfo.InvariantCulture),
                    ["outcome"] = "recorded",
                }));
        }

        // Counters only: the span carries the shape of the batch, the ledger rows carry the detail.
        activity?.SetTag("usage.batch.inserted", result.Inserted.Count);
        activity?.SetTag("usage.batch.duplicates", result.Duplicates.Count);

        return Result.Success(result);
    }
}
