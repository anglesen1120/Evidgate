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
/// <see cref="IQuotaGate"/>: one conditional statement that both checks and reserves, inside the
/// caller's tenant transaction.
/// <para>
/// A call within the limit increments <c>used</c> and answers Ok or Warn; a call that would pass it
/// writes nothing but the refusal stamp and answers Block, which the caller turns into 429 without
/// rolling its committed work back. A scope with no counter row has no configured limit and answers Ok
/// once the reservation found nothing to increment — the gate still ran.
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
    /// A non-positive quantity or a period that is not <c>YYYY-MM</c> is a caller bug: it is refused before
    /// any statement runs rather than silently granting or charging the wrong amount.
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

        if (!UsagePeriod.IsWellFormed(request.Period))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Period,
                "a quota check reserves usage of one YYYY-MM billing month");
        }

        if (!options.Value.Enabled)
        {
            return new QuotaCheckResult(QuotaDecision.Ok);
        }

        var orgId = scopes.Require().OrgId;
        var metric = UsageMetricWire.ToWireValue(request.Metric);

        var reserved = await store
            .ReserveAsync(orgId, request.Period, metric, request.ProjectId, request.Quantity, cancellationToken)
            .ConfigureAwait(false);

        if (reserved is not null)
        {
            return new QuotaCheckResult(
                reserved.IsAtOrAboveWarnLine ? QuotaDecision.Warn : QuotaDecision.Ok,
                reserved.Used,
                reserved.QuotaLimit);
        }

        var blocked = await store
            .MarkBlockedAsync(orgId, request.Period, metric, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        return blocked is null
            ? new QuotaCheckResult(QuotaDecision.Ok)
            : new QuotaCheckResult(
                QuotaDecision.Block,
                blocked.Used,
                blocked.QuotaLimit,
                RetryUntilNextPeriod(request.Period),
                $"/internal/operator/billing/orgs/{orgId:D}/quota-raise");
    }

    /// <summary>
    /// The wait a blocked caller is told: the next month starts the fresh counter that makes the same
    /// call fit again. At least one second, so a caller never reads <c>Retry-After: 0</c> as "retry now".
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
