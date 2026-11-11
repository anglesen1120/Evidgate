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
/// with no open month after it answers a conflict, and then nothing is written. A billing key that already holds a
/// different payload answers a conflict too: the store rolls the whole batch back, and nothing is metered or audited.
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

    /// <summary>
    /// The conflict code of a batch whose billing key already holds a different payload: the key is a permanent
    /// identity, so the batch wrote nothing and a retry of the same payload cannot succeed.
    /// </summary>
    internal const string BillingKeyConflictCode = "billing_key_conflict";

    /// <summary>Runs the batch.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<UsageBatchResult>> Handle(RecordUsageBatch command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Usage, nameof(RecordUsageBatch));

        // (a) The whole batch is validated before any statement runs, so a refused call writes nothing.
        if (RefuseInvalidEntries(command.Entries) is { } rejected)
        {
            return rejected;
        }

        // (b) An empty batch is a well-formed no-op: no statement, no audit row, three empty lists.
        if (command.Entries.Count == 0)
        {
            audit.Suppress();
            return Result.Success(new UsageBatchResult([], [], []));
        }

        // (c) The tenant comes from the open scope only (00 §5.4 rule: never from the request).
        var orgId = scopes.Require().OrgId;

        if (await RefuseUnknownProjectsAsync(orgId, command.Entries, cancellationToken).ConfigureAwait(false)
            is { } unknown)
        {
            return unknown;
        }

        var batch = UsageBatchRules.Prepare(command.Entries);

        // (d) One statement for the batch; the returning set is the inserted half of the answer. The duplicate keys of
        // the same call are counted in this transaction, so a refusal never leaves a tally behind. A row the insert guard
        // re-dated is billed in its new month, and a month with no open period after it refuses the whole insert.
        UsageLedgerWrite write;
        try
        {
            write = await store.RecordBatchAsync(orgId, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (LedgerNoOpenPeriodException refused)
        {
            // A failed Result writes no audit row: the stage records only a command that succeeds.
            return Result.Failure<UsageBatchResult>(Error.Conflict(NoOpenPeriodCode, refused.Message));
        }

        // A conflict is permanent and the store already rolled the whole batch back: nothing is billed, so the metering,
        // the re-date audit and the command's own audit object must not run either.
        if (write.Conflicts.Count > 0)
        {
            return ConflictRefusal(write.Conflicts.Count);
        }

        var result = UsageBatchOutcome.Merge(batch, write.Inserted);

        UsageMetering.Schedule(scopes.Require(), batch.Rows, result.Inserted);

        await DescribeAuditAsync(orgId, batch, write, result, cancellationToken).ConfigureAwait(false);

        // Counters only: the span carries the shape of the batch, the ledger rows carry the detail.
        activity?.SetTag("usage.batch.inserted", result.Inserted.Count);
        activity?.SetTag("usage.batch.duplicates", result.Duplicates.Count);

        return Result.Success(result);
    }

    /// <summary>Validates the batch, or answers null when it may be prepared.</summary>
    /// <param name="entries">The entries of the batch, or null when the caller sent none at all.</param>
    private static Result<UsageBatchResult>? RefuseInvalidEntries(IReadOnlyList<UsageEntryInput>? entries)
    {
        var violations = UsageBatchRules.Validate(entries);
        return violations.Count == 0 ? null : Rejected(violations);
    }

    /// <summary>
    /// Checks the projects in one read inside this same transaction before anything is written: a foreign-key violation
    /// would abort the caller's transaction, so an unknown (or other-org) project is refused per entry instead.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the request).</param>
    /// <param name="entries">The entries whose projects to check.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<Result<UsageBatchResult>?> RefuseUnknownProjectsAsync(
        Guid orgId,
        IReadOnlyList<UsageEntryInput> entries,
        CancellationToken cancellationToken)
    {
        var violations = await store.ValidateProjectsAsync(orgId, entries, cancellationToken).ConfigureAwait(false);
        return violations.Count == 0 ? null : Rejected(violations);
    }

    /// <summary>The 422 a batch answers: one detail per violation, indexed by entry position.</summary>
    /// <param name="violations">The violations of the batch, in request order.</param>
    private static Result<UsageBatchResult> Rejected(IReadOnlyList<UsageViolation> violations) =>
        Result.Failure<UsageBatchResult>(Error.Invalid(
            "the usage batch was rejected",
            [.. violations.Select(violation => new ErrorDetail(violation.Path, violation.Reason, violation.Index))]));

    /// <summary>The conflict a batch answers when any of its keys already claims another payload.</summary>
    /// <param name="conflictingKeys">How many keys conflict, as the message words it.</param>
    private static Result<UsageBatchResult> ConflictRefusal(int conflictingKeys)
    {
        var keys = conflictingKeys == 1
            ? "1 billing key"
            : $"{conflictingKeys.ToString(CultureInfo.InvariantCulture)} billing keys";

        return Result.Failure<UsageBatchResult>(Error.Conflict(
            BillingKeyConflictCode,
            $"the usage batch has {keys} already holding a different payload; the batch wrote nothing"));
    }

    /// <summary>
    /// The command's audit object: the rows this call wrote, or a suppressed entry when every key was already on file. A
    /// re-dated row is audited before the batch object is described, because the last description is the one the command's
    /// own audit entry carries and that entry must name the rows, not the move.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="batch">The prepared batch the store just ran.</param>
    /// <param name="write">What the store wrote.</param>
    /// <param name="result">The answer the command is about to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task DescribeAuditAsync(
        Guid orgId,
        UsageBatch batch,
        UsageLedgerWrite write,
        UsageBatchResult result,
        CancellationToken cancellationToken)
    {
        if (result.Inserted.Count == 0)
        {
            audit.Suppress();
            return;
        }

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
}
