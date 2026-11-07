using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// The operator switch of the quota gate. The section is bound once at composition and validated on
/// start, so a host either has a real gate or a deliberately switched-off one — never a stub.
/// </summary>
internal sealed class QuotaGateOptions
{
    /// <summary>
    /// The configuration section, in the repository's <c>EVIDGATE:</c>-prefixed form
    /// (<c>EVIDGATE__USAGE__QUOTAGATE__ENABLED</c> in the environment).
    /// </summary>
    internal const string Section = "EVIDGATE:Usage:QuotaGate";

    /// <summary>
    /// False makes checks answer <see cref="QuotaDecision.Ok"/> and peeks answer nothing without reading or writing a
    /// counter. Api and Worker must use the same switch value.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// <see cref="IQuotaGate"/>: one conditional statement that both checks and reserves every scope the call charges,
/// inside the caller's tenant transaction, and one more only when a scope the call charges has a counter it did not
/// fit into.
/// <para>
/// A call within the limit charges each of its counters and answers Ok or Warn; a call that would pass any of them
/// writes only the first refusal stamp and answers Block, which the caller answers as 429. That stamp commits only if
/// the caller commits, so a caller that must roll its own work back — the ingest route's refused batch — writes it
/// again afterwards through <c>IQuotaBlockStamp</c>, in a transaction of its own. The scopes are locked in one order
/// (the organization-wide counter, then the project's) and the statement charges all of them or none, so a refusal in
/// one scope leaves no usage behind in the other. A scope with no counter row has no configured limit and answers Ok on
/// its own; the gate still ran, and it says so after one statement. A call the increment found no room for is settled by
/// the second statement, which charges the scopes when an operator raise committed in between, so a raised counter never
/// refuses work it now has room for and never drops the usage it admitted.
/// </para>
/// <para>
/// A question leaves <see cref="QuotaCheckRequest.Period"/> null when it should be gated in the month of the caller's
/// open transaction. The gate resolves that month inside its first statement and answers it
/// (<see cref="QuotaCheckResult.Period"/>, <see cref="QuotaPeekResult.Period"/>), so one call names one month without a
/// second round trip, and a caller that must stamp the refusal after its own rollback builds the stamp's request from
/// it. The advisory <see cref="PeekAsync"/> reads a counter without reserving, stamping or carrying anything: a caller
/// that must decide before it parses asks that one, never treats a clear answer as an admission, and its different
/// result type keeps the two apart.
/// </para>
/// <para>
/// A refusal names the wait until the next period, measured from the database's transaction clock — the clock that
/// named the period — so a host whose clock runs ahead of the database cannot shorten it to a second of a month the
/// database still has open.
/// </para>
/// <para>
/// A month starts with no counter, so the gate carries each scope's previous month's limit and warning line into it
/// before answering, and only the scopes that need it: a configured limit covers every later month instead of lapsing
/// at the rollover. The carry runs before any counter is locked, and it creates every missing counter of the call in
/// lock order, so a rollover never waits for the organization's row while holding the project's. The call that moves a
/// counter's warning stamp from null to a value also enqueues one warning task per such counter in the same transaction,
/// so the announcement and the counter it announces commit or roll back together.
/// </para>
/// </summary>
internal sealed class QuotaGate(
    ITenantScopeAccessor scopes,
    QuotaCounterStore store,
    IOutboxWriter outbox,
    IOptions<QuotaGateOptions> options) : IQuotaGate
{
    /// <inheritdoc />
    public async Task<QuotaPeekResult> PeekAsync(
        QuotaCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        QuotaCheckRequestRules.EnsureValid(request);

        if (!options.Value.Enabled)
        {
            return QuotaPeekResult.Clear;
        }

        var orgId = scopes.Require().OrgId;
        var refused = await store.PeekAsync(
                orgId, request.Period, UsageMetricWire.ToWireValue(request.Metric),
                QuotaScopeKeys.Of(request), request.Quantity, cancellationToken)
            .ConfigureAwait(false);

        return refused is null
            ? QuotaPeekResult.Clear
            : new QuotaPeekResult(
                true,
                refused.Used,
                refused.QuotaLimit,
                UsagePeriod.RetryUntilNextPeriod(refused.Period, refused.DatabaseNow),
                RaisePath(orgId),
                QuotaScopeKeys.KindOf(refused.ScopeKey),
                refused.Period,
                refused.Unstamped);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// A non-positive quantity, or a period that is neither null (the caller's own month) nor one from
    /// <c>2000-01</c> to <c>2999-12</c>, is a caller bug: it is refused before any statement runs rather than
    /// silently granting or charging the wrong amount.
    /// </exception>
    public async Task<QuotaCheckResult> CheckAsync(
        QuotaCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        QuotaCheckRequestRules.EnsureValid(request);

        if (!options.Value.Enabled)
        {
            return new QuotaCheckResult(QuotaDecision.Ok);
        }

        var orgId = scopes.Require().OrgId;
        var metric = UsageMetricWire.ToWireValue(request.Metric);
        var scopeKeys = QuotaScopeKeys.Of(request);

        var answer = await ReserveAsync(orgId, request.Period, request.Quantity, metric, scopeKeys, cancellationToken)
            .ConfigureAwait(false);

        if (answer.Count == 0)
        {
            // No counter for the scope and none earlier to carry: no configured limit, so no month answers either.
            return new QuotaCheckResult(QuotaDecision.Ok);
        }

        // The first statement names the month when the caller left it open; the carry, the second charge, the settle
        // and the answer all name that month, so a call across a month end stays in one month throughout.
        var period = answer[0].Period;

        if (answer.Any(row => row.NeedsCarry))
        {
            // Carried before any scope is locked, and one statement for every missing scope: the reservation holds no
            // counter while it creates the others, so a rollover cannot take the project's lock and then wait for the
            // organization's, which is the order a second caller's charge takes.
            await store
                .CarryForwardAsync(orgId, period, metric, scopeKeys, cancellationToken)
                .ConfigureAwait(false);

            answer = await ReserveAsync(orgId, period, request.Quantity, metric, scopeKeys, cancellationToken)
                .ConfigureAwait(false);

            if (answer.Any(row => row.NeedsCarry))
            {
                throw new InvalidOperationException(
                    $"the quota counter for {period} still has no row after the carry-forward");
            }
        }

        return answer.Any(row => !row.Reserved)
            ? await SettledAsync(orgId, period, request.Quantity, metric, scopeKeys, cancellationToken)
                .ConfigureAwait(false)
            : await ReservedAsync(period, metric, scopeKeys, answer, cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<QuotaCounterRow>> ReserveAsync(
        Guid orgId,
        string? period,
        decimal quantity,
        string metric,
        Guid[] scopeKeys,
        CancellationToken cancellationToken) =>
        store.ReserveAsync(orgId, period, metric, scopeKeys, quantity, cancellationToken);

    /// <summary>The charged answer, and one warning task per counter whose warning transition this call owns.</summary>
    private async Task<QuotaCheckResult> ReservedAsync(
        string period,
        string metric,
        Guid[] scopeKeys,
        IReadOnlyList<QuotaCounterRow> answer,
        CancellationToken cancellationToken)
    {
        foreach (var warned in answer.Where(row => row.WarnedNow))
        {
            await EnqueueWarningAsync(
                    period, metric, warned.ScopeKey, warned.Id, warned.Used, warned.QuotaLimit, warned.WarnRatio,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var tightest = Tightest(answer, scopeKeys);

        return new QuotaCheckResult(
            answer.Any(row => row.IsAtOrAboveWarnLine) ? QuotaDecision.Warn : QuotaDecision.Ok,
            tightest.Used,
            tightest.QuotaLimit,
            Period: period);
    }

    /// <summary>
    /// The settle statement, which is the second chance a call gets after an operator raise committed
    /// between the two statements — and the second place a warning transition can happen.
    /// </summary>
    private async Task<QuotaCheckResult> SettledAsync(
        Guid orgId,
        string period,
        decimal quantity,
        string metric,
        Guid[] scopeKeys,
        CancellationToken cancellationToken)
    {
        var settled = await store
            .SettleAsync(orgId, period, metric, scopeKeys, quantity, cancellationToken)
            .ConfigureAwait(false);

        if (settled.Count == 0)
        {
            return new QuotaCheckResult(QuotaDecision.Ok, Period: period);
        }

        foreach (var warned in settled.Where(row => row.WarnedNow))
        {
            await EnqueueWarningAsync(period, metric, warned.ScopeKey, warned.Id, warned.Used, warned.QuotaLimit,
                    warned.WarnRatio, cancellationToken)
                .ConfigureAwait(false);
        }

        var refused = settled.Where(row => row.Blocked).ToList();
        if (refused.Count > 0)
        {
            // Both scopes can refuse one call. The answer names the first in lock order, which is the
            // organization-wide counter: the limit an operator raises to unblock every project at once.
            var refusing = refused.OrderBy(row => LockRank(row, scopeKeys)).First();

            return new QuotaCheckResult(
                QuotaDecision.Block,
                refusing.Used,
                refusing.QuotaLimit,
                UsagePeriod.RetryUntilNextPeriod(period, refusing.DatabaseNow),
                RaisePath(orgId),
                QuotaScopeKeys.KindOf(refusing.ScopeKey),
                period);
        }

        var tightest = Tightest(settled, scopeKeys);

        return new QuotaCheckResult(
            settled.Any(row => row.IsAtOrAboveWarnLine) ? QuotaDecision.Warn : QuotaDecision.Ok,
            tightest.Used,
            tightest.QuotaLimit,
            Period: period);
    }

    /// <summary>
    /// The scope whose numbers answer the call: the one with the least room left, so a verdict and the figures beside
    /// it always describe the same counter. A tie is answered in lock order (<paramref name="scopeKeys"/>), so the same
    /// call always reports the same scope.
    /// </summary>
    /// <param name="rows">The counters the statement answered for the scopes.</param>
    /// <param name="scopeKeys">The scopes the call charged, the tenant's first.</param>
    /// <returns>The row the call's numbers come from.</returns>
    private static T Tightest<T>(IReadOnlyList<T> rows, Guid[] scopeKeys)
        where T : IQuotaScopeNumbers
    {
        var tightest = rows[0];

        foreach (var row in rows)
        {
            if (row.Headroom < tightest.Headroom
                || (row.Headroom == tightest.Headroom && LockRank(row, scopeKeys) < LockRank(tightest, scopeKeys)))
            {
                tightest = row;
            }
        }

        return tightest;
    }

    /// <summary>Where one scope sits in the single order every statement locks the counters in.</summary>
    /// <param name="row">The counter the statement answered.</param>
    /// <param name="scopeKeys">The scopes the call charged, the tenant's first.</param>
    /// <returns>The scope's index, or <see cref="int.MaxValue"/> for a row outside the call's scopes.</returns>
    private static int LockRank(IQuotaScopeNumbers row, Guid[] scopeKeys) =>
        Array.IndexOf(scopeKeys, row.ScopeKey) is var rank && rank >= 0 ? rank : int.MaxValue;

    /// <summary>
    /// Writes the warning task through the outbox on the caller's connection, so a rolled-back batch
    /// leaves neither the counter change nor the announcement behind.
    /// </summary>
    private Task<Guid> EnqueueWarningAsync(
        string period,
        string metric,
        Guid scopeKey,
        Guid counterId,
        decimal used,
        decimal quotaLimit,
        decimal warnRatio,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            QuotaWarningTask.TaskType,
            // The payload names the counter's own project, which is null for the organization-wide counter: a
            // consumer repairs the row it announces and must not be told the caller's project instead.
            QuotaWarningTask.PayloadJson(new QuotaWarningPayload(
                counterId, QuotaScopeKeys.ProjectOf(scopeKey), period, metric, used, quotaLimit, warnRatio)),
            QuotaWarningTask.AggregateType,
            counterId,
            QuotaWarningTask.IdempotencyKey(counterId),
            cancellationToken);

    /// <summary>The audited operator path a blocked caller is told about.</summary>
    private static string RaisePath(Guid orgId) => $"/internal/operator/billing/orgs/{orgId:D}/quota-raise";
}
