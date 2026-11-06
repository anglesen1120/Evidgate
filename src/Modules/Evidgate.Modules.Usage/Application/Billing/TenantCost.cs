using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// One input of a tenant's monthly cost: what was measured, from which source, and why nothing could be measured
/// when nothing was.
/// </summary>
/// <remarks>
/// <see cref="Quantity"/> and <see cref="UnavailableReason"/> are exclusive: a line with source rows carries the
/// sum those rows net to — zero included, because a measured zero is a measurement — and a line without any is
/// null with the reason a producer is missing, never 0.
/// </remarks>
/// <param name="Line">The line's name, fixed per input.</param>
/// <param name="Quantity">The sum of the source rows, or null when the source holds none in the period.</param>
/// <param name="Unit">What the quantity counts: <c>share</c>, <c>gb_month</c>, <c>gb</c>, <c>tokens</c> or <c>hours</c>.</param>
/// <param name="Source">Where the number comes from, named table first.</param>
/// <param name="UnavailableReason">Why the line has no number, or null when it has one.</param>
internal sealed record TenantCostLine(
    string Line,
    decimal? Quantity,
    string Unit,
    string Source,
    string? UnavailableReason);

/// <summary>
/// What one tenant cost in one closed period: the five inputs the cost model is built on, in a fixed order so two
/// reads of an unchanged period answer the same document.
/// </summary>
/// <param name="Period">The period, <c>YYYY-MM</c>.</param>
/// <param name="Lines">The five lines, in the order the cost model lists them.</param>
internal sealed record TenantCost(string Period, IReadOnlyList<TenantCostLine> Lines);

/// <summary>
/// One tenant's cost of one closed period, read on demand:
/// <c>GET /internal/operator/orgs/{o}/cost?period=</c>.
/// <para>
/// A cost line is either a sum the ledger states or null with the reason its source is missing: reporting a
/// missing input as 0 would claim a measurement nobody made, so an absence is never a number here.
/// </para>
/// </summary>
/// <remarks>
/// The ledger's rows are the cost basis; the AI line falls back to the healing gateway's own attempt ledger only
/// while the ledger holds no <c>ai_tokens</c> row for the period, and never sums the two. The read is audited like
/// the other billing reads — one row per read, in the read's own transaction, naming the period and how many lines
/// it could state, never a quantity.
/// </remarks>
internal sealed class CostPerTenantReader(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ITenantLimits limits,
    UsageReadStore store,
    TimeProvider time)
{
    /// <summary>The audited action of one cost read.</summary>
    internal const string Action = "usage.cost_per_tenant_read";

    /// <summary>The object type the read's audit row names, as the ledger reads beside it do.</summary>
    internal const string ObjectType = "usage_ledger_entry";

    /// <summary>The API/DB share line: a capacity figure no counter produces yet.</summary>
    private const string ApiDbShareLine = "api_db_share";

    /// <summary>The stored artifact GB-month line.</summary>
    private const string ObjectGbMonthLine = "object_gb_month";

    /// <summary>The artifact egress line.</summary>
    private const string EgressGbLine = "egress_gb";

    /// <summary>The model-token line.</summary>
    private const string AiTokensLine = "ai_tokens";

    /// <summary>The support-time line.</summary>
    private const string SupportHoursLine = "support_hours";

    /// <summary>The prefix that names the ledger as a line's source.</summary>
    private const string LedgerSource = "usage_ledger_entry";

    /// <summary>The healing gateway's attempt ledger, the AI line's stand-in source.</summary>
    private const string HealingSource = "healing_llm_call.tokens";

    /// <summary>The source the API/DB share line would read, which holds no counter in this build.</summary>
    private const string CapacitySource = "capacity_counters:api_db_share";

    /// <summary>
    /// Validates the period, opens the org's scope, reads the five inputs and writes the audit row of that read
    /// inside the same transaction.
    /// </summary>
    /// <param name="orgId">The org the route names.</param>
    /// <param name="operatorId">The authenticated operator, the audit row's actor.</param>
    /// <param name="period">The period, <c>YYYY-MM</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<TenantCost>> ExecuteAsync(
        Guid orgId,
        Guid operatorId,
        string? period,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Usage,
            nameof(CostPerTenantReader));

        var validated = StatementIssueRules.ValidatePeriod(period);
        if (validated.Error is { } invalid)
        {
            return Result.Failure<TenantCost>(invalid);
        }

        using var unit = audit.BeginUnit();
        await using var scope = await scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        if (await limits.ForOrganizationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<TenantCost>(Error.NotFound("no such organization"));
        }

        if (!StatementIssueRules.IsClosed(validated.Value, time.GetUtcNow()))
        {
            return Result.Failure<TenantCost>(StatementIssueRules.OpenPeriod());
        }

        if (!UsagePeriod.TryMonthRange(validated.Value, out var monthStart, out var monthEnd))
        {
            return Result.Failure<TenantCost>(Error.Invalid(
                "the period is outside the range a cost month can be read for",
                [new ErrorDetail("period", "format")]));
        }

        var rows = await store
            .ReadTenantCostAsync(orgId, validated.Value, monthStart, monthEnd, cancellationToken)
            .ConfigureAwait(false);

        var cost = Assemble(validated.Value, rows);

        DescribeAudit(orgId, operatorId, cost);
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(cost);
    }

    /// <summary>
    /// Turns the read's rows into the five lines, in the cost model's order. A metric the read did not return holds
    /// no row in the period, which is the difference between an unavailable line and a measured zero.
    /// </summary>
    /// <param name="period">The period read.</param>
    /// <param name="rows">The ledger's per-metric sums and the gateway's month.</param>
    private static TenantCost Assemble(string period, IReadOnlyList<TenantCostRow> rows)
    {
        var ledger = rows
            .Where(row => string.Equals(row.Kind, UsageReadStore.LedgerKind, StringComparison.Ordinal))
            .ToDictionary(row => row.Line, StringComparer.Ordinal);
        var healing = rows.Single(row =>
            string.Equals(row.Kind, UsageReadStore.HealingKind, StringComparison.Ordinal));

        TenantCostLine[] lines =
        [
            new TenantCostLine(
                ApiDbShareLine,
                null,
                "share",
                CapacitySource,
                "the API and DB capacity counters this line divides among tenants are not built, "
                + "so no per-tenant share of the platform's cost can be measured"),
            FromLedger(ledger, UsageMetric.ArtifactGbMonth, ObjectGbMonthLine, "gb_month"),
            FromLedger(ledger, UsageMetric.EgressGb, EgressGbLine, "gb"),
            Tokens(ledger, healing),
            FromLedger(ledger, UsageMetric.SupportHours, SupportHoursLine, "hours"),
        ];

        return new TenantCost(period, lines);
    }

    /// <summary>
    /// One line the ledger sums itself: the metric's rows of the period, corrections included.
    /// </summary>
    /// <param name="ledger">The ledger's sums by wire metric name.</param>
    /// <param name="metric">The metric the line reports.</param>
    /// <param name="line">The line's name.</param>
    /// <param name="unit">What the quantity counts.</param>
    private static TenantCostLine FromLedger(
        Dictionary<string, TenantCostRow> ledger,
        UsageMetric metric,
        string line,
        string unit)
    {
        var wire = UsageMetricWire.ToWireValue(metric);
        var source = $"{LedgerSource}:{wire}";

        return ledger.TryGetValue(wire, out var row)
            ? new TenantCostLine(line, row.Quantity, unit, source, null)
            : new TenantCostLine(line, null, unit, source, UsageMetricAvailability.UnavailableReason(metric));
    }

    /// <summary>
    /// The token line: the ledger's <c>ai_tokens</c> rows while it holds one, otherwise every attempt the healing
    /// gateway recorded in the month — a failed attempt burned its tokens too, so every status counts. The two
    /// sources are never added together, which would bill the same tokens twice once the ledger's emitter lands.
    /// </summary>
    /// <param name="ledger">The ledger's sums by wire metric name.</param>
    /// <param name="healing">The gateway's token sum and attempt count for the month.</param>
    private static TenantCostLine Tokens(
        Dictionary<string, TenantCostRow> ledger,
        TenantCostRow healing)
    {
        var wire = UsageMetricWire.ToWireValue(UsageMetric.AiTokens);

        if (ledger.TryGetValue(wire, out var row))
        {
            return new TenantCostLine(AiTokensLine, row.Quantity, "tokens", $"{LedgerSource}:{wire}", null);
        }

        return healing.RowCount > 0
            ? new TenantCostLine(AiTokensLine, healing.Quantity, "tokens", HealingSource, null)
            : new TenantCostLine(
                AiTokensLine,
                null,
                "tokens",
                $"{LedgerSource}:{wire}",
                UsageMetricAvailability.UnavailableReason(UsageMetric.AiTokens));
    }

    /// <summary>
    /// Records which period was read and how many of the five inputs could be stated. No quantity reaches the audit
    /// chain: this row says what was read, not what it cost.
    /// </summary>
    /// <param name="orgId">The tenant read.</param>
    /// <param name="operatorId">The operator the read is attributed to.</param>
    /// <param name="cost">The answer the read returned.</param>
    private void DescribeAudit(Guid orgId, Guid operatorId, TenantCost cost)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = cost.Period,
            ["count"] = cost.Lines
                .Count(line => line.Quantity is not null)
                .ToString(CultureInfo.InvariantCulture),
        };

        audit.Describe(new AuditObject(ObjectType, orgId.ToString("D"), Metadata: metadata));
        audit.Record(Action, new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }
}
