using System.Globalization;
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
    /// False answers <see cref="QuotaDecision.Ok"/> without reading or writing a counter, the explicit
    /// operator escape hatch for a pilot whose limits are not loaded yet.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// <see cref="IQuotaGate"/>: one conditional statement that both checks and reserves, inside the caller's
/// tenant transaction, and one more only when the scope has a counter the call did not fit into.
/// <para>
/// A call within the limit increments <c>used</c> and answers Ok or Warn; a call that would pass it
/// writes only the first refusal stamp and answers Block, which the caller turns into 429 without
/// rolling its committed work back. A scope with no counter row has no configured limit and answers Ok
/// — the gate still ran, and it says so after one statement. A call the increment found no room for is
/// settled by the second statement, which reserves it when an operator raise committed in between, so a
/// raised counter never refuses work it now has room for and never drops the usage it admitted.
/// </para>
/// </summary>
internal sealed class QuotaGate(
    ITenantScopeAccessor scopes,
    QuotaCounterStore store,
    IOptions<QuotaGateOptions> options,
    TimeProvider time) : IQuotaGate
{
    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// A non-positive quantity or a period outside <c>2000-01</c>..<c>2999-12</c> is a caller bug: it is
    /// refused before any statement runs rather than silently granting or charging the wrong amount.
    /// </exception>
    public async Task<QuotaCheckResult> CheckAsync(
        QuotaCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Quantity,
                "a quota check reserves a strictly positive quantity");
        }

        if (!UsagePeriod.IsQuotaPeriod(request.Period))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Period,
                "a quota check reserves usage of one YYYY-MM billing month from 2000-01 to 2999-12");
        }

        if (!options.Value.Enabled)
        {
            return new QuotaCheckResult(QuotaDecision.Ok);
        }

        var orgId = scopes.Require().OrgId;
        var metric = UsageMetricWire.ToWireValue(request.Metric);

        var answer = await store
            .ReserveAsync(orgId, request.Period, metric, request.ProjectId, request.Quantity, cancellationToken)
            .ConfigureAwait(false);

        if (answer is null)
        {
            return new QuotaCheckResult(QuotaDecision.Ok);
        }

        if (answer.Reserved)
        {
            return new QuotaCheckResult(
                answer.IsAtOrAboveWarnLine ? QuotaDecision.Warn : QuotaDecision.Ok,
                answer.Used,
                answer.QuotaLimit);
        }

        var settled = await store
            .StampRefusalAsync(orgId, request.Period, metric, request.ProjectId, request.Quantity, cancellationToken)
            .ConfigureAwait(false);

        if (settled is null)
        {
            return new QuotaCheckResult(QuotaDecision.Ok);
        }

        if (!settled.Blocked)
        {
            return new QuotaCheckResult(
                settled.IsAtOrAboveWarnLine ? QuotaDecision.Warn : QuotaDecision.Ok,
                settled.Used,
                settled.QuotaLimit);
        }

        return new QuotaCheckResult(
            QuotaDecision.Block,
            settled.Used,
            settled.QuotaLimit,
            RetryUntilNextPeriod(request.Period),
            $"/internal/operator/billing/orgs/{orgId:D}/quota-raise");
    }

    /// <summary>
    /// The wait a blocked caller is told: the next month starts the fresh counter that makes the same
    /// call fit again. At least one second, so a caller never reads <c>Retry-After: 0</c> as "retry now".
    /// The caller has already restricted the period to 2000-01..2999-12, so both the parse and the
    /// following month stay inside <see cref="DateTimeOffset"/> and this method never throws.
    /// </summary>
    private TimeSpan RetryUntilNextPeriod(string period)
    {
        var year = int.Parse(period.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(period.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var next = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var until = next - time.GetUtcNow();

        return until < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : until;
    }
}
