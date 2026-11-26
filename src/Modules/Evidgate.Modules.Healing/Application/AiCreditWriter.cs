using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Domain;
using Evidgate.Modules.Healing.Infrastructure.Persistence;
using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Healing.Application;

/// <summary>One credit entry a caller asks to append.</summary>
/// <param name="Kind">The entry kind.</param>
/// <param name="AmountMicro">The amount in micro-units: positive, except an adjustment, which is non-zero.</param>
/// <param name="CallId">The healing call a reserve, consume or refund belongs to; null for every other kind.</param>
/// <param name="IdempotencyKey">
/// The caller's replay key of a call-less entry (1..<see cref="CreditMath.MaxIdempotencyKeyLength"/> characters); null for
/// a call-bound entry, whose key is the call and the kind.
/// </param>
/// <param name="Reason">Optional reason, 1..500 characters.</param>
/// <param name="ProjectId">The project a call-less entry is metered for, or null for the organization.</param>
internal sealed record CreditEntryRequest(
    CreditEntryKind Kind,
    long AmountMicro,
    Guid? CallId,
    string? IdempotencyKey,
    string? Reason,
    Guid? ProjectId = null);

/// <summary>What an append came to.</summary>
/// <param name="EntryId">The entry that holds the write: the new one, or the earlier one a replay matched.</param>
/// <param name="Replayed">True when the key was already on file with the same payload and nothing was written.</param>
/// <param name="AvailableMicro">The account's available part after the call.</param>
/// <param name="ReservedMicro">The account's reserved part after the call.</param>
internal sealed record CreditAppendOutcome(Guid EntryId, bool Replayed, long AvailableMicro, long ReservedMicro);

/// <summary>
/// The one code path that changes an AI credit balance (migration 0135). It never opens, commits or rolls back a
/// transaction: the account lock, the usage ledger row, the entry and the audit row all run on the caller's tenant
/// transaction, so a failure anywhere leaves none of them. Writers of one account serialize on its row lock, so the
/// balance the math reads is the balance it writes and two calls cannot both spend the same credit.
/// </summary>
internal sealed class AiCreditWriter(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IUsageLedger ledger,
    IAuditTrail audit,
    IRequestPrincipalAccessor principals,
    TimeProvider time)
{
    /// <summary>The audited action of one appended entry.</summary>
    internal const string Action = "ai_credit.entry_appended";

    /// <summary>The object type the audit row names.</summary>
    internal const string ObjectType = "ai_credit_entry";

    /// <summary>The refusal code of a reserve the available balance cannot cover.</summary>
    internal const string ExhaustedCode = "ai_credit_exhausted";

    /// <summary>The refusal code of a billing key that already holds a different payload.</summary>
    internal const string KeyConflictCode = "ai_credit_key_conflict";

    /// <summary>The ledger unit of an entry; the kind suffix keeps the seven kinds apart in one metric.</summary>
    internal static string LedgerUnit(CreditEntryKind kind) => "micro_credit_" + CreditMath.Wire(kind);

    /// <summary>The replay key an entry is stored under, which is also its usage ledger billing key.</summary>
    /// <param name="request">The request.</param>
    internal static string BillingKey(CreditEntryRequest request) => request.CallId is { } call
        ? $"ai:{call:D}:{CreditMath.Wire(request.Kind)}"
        : $"ai:{CreditMath.Wire(request.Kind)}:{request.IdempotencyKey}";

    /// <summary>Appends <paramref name="request"/> to the organization's account.</summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="request">The entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome, or a refusal that wrote nothing.</returns>
    internal async ValueTask<Result<CreditAppendOutcome>> AppendAsync(
        Guid orgId,
        CreditEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureScope(orgId);

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var projectId = request.ProjectId;
        if (request.CallId is { } callId)
        {
            projectId = await db.ScalarAsync<Guid?>(AiCreditSql.CallProject, new { org_id = orgId, id = callId }, cancellationToken)
                .ConfigureAwait(false);
            if (projectId is null)
            {
                return Result.Failure<CreditAppendOutcome>(Error.NotFound("no healing call with that id is visible to this organization"));
            }
        }

        if (request.Kind == CreditEntryKind.Grant)
        {
            await db.ExecuteAsync(AiCreditSql.EnsureAccount, new { id = Guid.CreateVersion7(), org_id = orgId }, cancellationToken)
                .ConfigureAwait(false);
        }

        var account = await db.QuerySingleOrDefaultAsync<AccountRow>(AiCreditSql.LockAccount, new { org_id = orgId }, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return Refuse(request.Kind);
        }

        var billingKey = BillingKey(request);
        var hash = PayloadHash(request);
        var existing = await db.QuerySingleOrDefaultAsync<EntryRow>(
            AiCreditSql.FindEntry,
            new { org_id = orgId, billing_key = billingKey },
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return string.Equals(existing.PayloadHash, hash, StringComparison.Ordinal)
                ? Result.Success(new CreditAppendOutcome(existing.Id, true, account.BalanceMicro, account.ReservedMicro))
                : KeyConflict();
        }

        return await WriteAsync(orgId, request, account, billingKey, hash, projectId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<CreditAppendOutcome>> WriteAsync(
        Guid orgId,
        CreditEntryRequest request,
        AccountRow account,
        string billingKey,
        string hash,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (request.CallId is { } call && request.Kind is CreditEntryKind.Consume or CreditEntryKind.Refund
            && !await FitsTheReserveAsync(orgId, call, request, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(request.Kind);
        }

        if (!CreditMath.TryApply(new CreditBalance(account.BalanceMicro, account.ReservedMicro), request.Kind, request.AmountMicro, out var next))
        {
            return Refuse(request.Kind);
        }

        // The ledger row goes first: it throws when no period is open and answers a conflict for a key that holds other
        // work, and either way nothing of the entry has been written yet.
        var recorded = await ledger.RecordAsync(LedgerRequest(request, billingKey, projectId), cancellationToken).ConfigureAwait(false);
        if (recorded.Conflicts.Count > 0)
        {
            return KeyConflict();
        }

        if (recorded.Inserted.Count != 1)
        {
            throw new InvalidOperationException("the usage ledger already holds this credit entry's billing key without a credit entry");
        }

        var actor = AuditActorResolver.From(principals.Current);
        var entryId = Guid.CreateVersion7();
        await db.ExecuteAsync(
            AiCreditSql.UpdateAccount,
            new { org_id = orgId, id = account.Id, balance_micro = next.AvailableMicro, reserved_micro = next.ReservedMicro },
            cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            AiCreditSql.InsertEntry,
            new
            {
                id = entryId,
                org_id = orgId,
                account_id = account.Id,
                kind = CreditMath.Wire(request.Kind),
                amount_micro = request.AmountMicro,
                call_id = request.CallId,
                billing_key = billingKey,
                payload_hash = hash,
                reason = request.Reason,
                actor = $"{actor.Type}:{actor.Id}",
            },
            cancellationToken).ConfigureAwait(false);

        await AuditAsync(orgId, entryId, request.Kind, actor, cancellationToken).ConfigureAwait(false);

        return Result.Success(new CreditAppendOutcome(entryId, false, next.AvailableMicro, next.ReservedMicro));
    }

    /// <summary>
    /// What an entry means, as the digest a replay must reproduce; the actor and the time are not part of it. The amount
    /// is hashed as text because the canonical form of a JSON number is a binary64, which cannot tell two large amounts apart.
    /// </summary>
    internal static string PayloadHash(CreditEntryRequest request) => CanonicalHash.Compute(JsonSerializer.Serialize(new
    {
        kind = CreditMath.Wire(request.Kind),
        amount_micro = request.AmountMicro.ToString(CultureInfo.InvariantCulture),
        call_id = request.CallId,
        project_id = request.CallId is null ? request.ProjectId : null,
        reason = request.Reason,
    }));

    /// <summary>
    /// A settlement may only draw on its own call's reserve: the account-wide reserved total is shared by every call, so
    /// without this a retry of one call would consume another call's hold. Runs under the account lock.
    /// </summary>
    private async ValueTask<bool> FitsTheReserveAsync(Guid orgId, Guid callId, CreditEntryRequest request, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<CallEntryRow>(AiCreditSql.CallEntries, new { org_id = orgId, call_id = callId }, cancellationToken)
            .ConfigureAwait(false);

        decimal reserved = 0;
        decimal settled = request.AmountMicro;
        foreach (var row in rows)
        {
            if (row.Kind == CreditMath.Wire(CreditEntryKind.Reserve))
            {
                reserved += row.AmountMicro;
            }
            else
            {
                settled += row.AmountMicro;
            }
        }

        return settled <= reserved;
    }

    private static Result<CreditAppendOutcome>? Validate(CreditEntryRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || !CreditMath.IsValidAmount(request.Kind, request.AmountMicro))
        {
            return Invalid("/amount_micro", "the amount must be positive (non-zero for an adjustment) and fit a 64-bit integer");
        }

        if (CreditMath.IsCallBound(request.Kind) != request.CallId.HasValue)
        {
            return Invalid("/call_id", "a reserve, consume or refund names its call; no other kind may");
        }

        if (request.CallId.HasValue == (request.IdempotencyKey is not null))
        {
            return Invalid("/idempotency_key", "a call-less entry carries a replay key; a call-bound entry is keyed by its call");
        }

        if (request.IdempotencyKey is { } key
            && (!CreditMath.IsStorable(key) || CreditMath.CodePointLength(key) is 0 or > CreditMath.MaxIdempotencyKeyLength))
        {
            return Invalid("/idempotency_key", "the replay key must be 1 to 150 storable characters");
        }

        if (request.Reason is { } reason
            && (!CreditMath.IsStorable(reason) || CreditMath.CodePointLength(reason) is 0 or > CreditMath.MaxReasonLength))
        {
            return Invalid("/reason", "the reason must be 1 to 500 storable characters");
        }

        return null;
    }

    private static Result<CreditAppendOutcome> Invalid(string path, string message) =>
        Result.Failure<CreditAppendOutcome>(Error.Invalid(message, [new ErrorDetail(path, "invalid")]));

    private static Result<CreditAppendOutcome> KeyConflict() =>
        Result.Failure<CreditAppendOutcome>(Error.Conflict(KeyConflictCode, "that replay key already holds a different credit entry"));

    /// <summary>A reserve the balance cannot cover is the tenant-visible refusal; any other kind that cannot apply is a bad request.</summary>
    private static Result<CreditAppendOutcome> Refuse(CreditEntryKind kind) => kind == CreditEntryKind.Reserve
        ? Result.Failure<CreditAppendOutcome>(Error.Unprocessable(ExhaustedCode, "the available AI credit does not cover this reserve"))
        : Result.Failure<CreditAppendOutcome>(Error.Unprocessable(
            "ai_credit_refused",
            "the entry would leave the account below zero or overflow it",
            [new ErrorDetail("/kind", CreditMath.Wire(kind))]));

    private RecordUsageBatchRequest LedgerRequest(CreditEntryRequest request, string billingKey, Guid? projectId)
    {
        var now = time.GetUtcNow();

        return new RecordUsageBatchRequest(
        [
            new UsageEntryInput(
                billingKey,
                UsageMetric.AiCredit,
                projectId,
                Math.Abs((decimal)request.AmountMicro),
                LedgerUnit(request.Kind),
                now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                now),
        ]);
    }

    private async Task AuditAsync(Guid orgId, Guid entryId, CreditEntryKind kind, AuditActor actor, CancellationToken cancellationToken)
    {
        var ownsUnit = !audit.HasUnit;
        using var unit = ownsUnit ? audit.BeginUnit() : null;

        audit.Describe(new AuditObject(
            ObjectType,
            entryId.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = CreditMath.Wire(kind) }));
        audit.Record(Action, actor);

        if (ownsUnit)
        {
            await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureScope(Guid orgId)
    {
        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }
    }

    private sealed class AccountRow
    {
        public Guid Id { get; set; }

        public long BalanceMicro { get; set; }

        public long ReservedMicro { get; set; }
    }

    private sealed class CallEntryRow
    {
        public string Kind { get; set; } = string.Empty;

        public long AmountMicro { get; set; }
    }

    private sealed class EntryRow
    {
        public Guid Id { get; set; }

        public string PayloadHash { get; set; } = string.Empty;
    }
}
