using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Mediator;

namespace Evidgate.Modules.Usage.Application.Commands;

/// <summary>
/// Raises one scope's quota limit (12.3.1 §5): <c>POST /internal/operator/billing/orgs/{o}/quota-raise</c> and
/// the in-process shape of the same write. The organization is the open scope's tenant, never a value of the
/// request, and the raise carries a reason because it changes what a pilot may consume.
/// </summary>
/// <param name="ProjectId">The project whose counter moves, or null for the tenant-level counter.</param>
/// <param name="Period">The billing month, <c>YYYY-MM</c>.</param>
/// <param name="Metric">The metered metric the limit applies to.</param>
/// <param name="NewLimit">The new limit; it must exceed the stored one, so a raise can never lower a limit below usage.</param>
/// <param name="Reason">Why the limit was raised, 1..200 characters of plain text; surrounding whitespace is trimmed before the audit row keeps it.</param>
internal sealed record RaiseQuotaLimit(
    Guid? ProjectId,
    string? Period,
    UsageMetric Metric,
    decimal NewLimit,
    string? Reason) : ICommand<Result<QuotaRaiseResult>>;

/// <summary>
/// The request rules of a raise. A refused raise writes nothing and answers one 00 §9.2 detail per field,
/// so the operator learns which member to fix; a valid raise is answered as the prepared change the writer
/// applies, which keeps the rules and the statement apart.
/// </summary>
internal static class QuotaRaiseRules
{
    /// <summary>Longest reason the audit row's metadata value accepts.</summary>
    internal const int MaxReasonLength = 200;

    /// <summary>The message every refusal of this command carries.</summary>
    internal const string InvalidMessage = "the quota raise was rejected";

    /// <summary>
    /// Validates the typed command and turns it into the wire change. The metric member is checked before it
    /// is mapped, so an undeclared member is a refusal rather than an exception on the way to SQL; the reason is
    /// trimmed and checked against the audit row's own value rule here, because that row is written after the
    /// counter and a reason the row refuses would otherwise be a failure with the limit already moved.
    /// </summary>
    /// <param name="command">The raise to validate.</param>
    internal static Result<QuotaLimitChange> Prepare(RaiseQuotaLimit command)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<ErrorDetail>? violations = null;

        if (command.ProjectId == Guid.Empty)
        {
            (violations ??= []).Add(new ErrorDetail("project_id", "invalid_value"));
        }

        ValidatePeriod(command.Period, ref violations);
        ValidateMetric(Enum.IsDefined(command.Metric), ref violations);

        if (command.NewLimit <= 0)
        {
            (violations ??= []).Add(new ErrorDetail("new_limit", "invalid_value"));
        }

        var reason = NormalizeReason(command.Reason, ref violations);

        if (violations is not null)
        {
            return Result.Failure<QuotaLimitChange>(Error.Invalid(InvalidMessage, [.. violations]));
        }

        return Result.Success(new QuotaLimitChange(
            command.ProjectId,
            command.Period ?? string.Empty,
            UsageMetricWire.ToWireValue(command.Metric),
            command.NewLimit,
            reason ?? string.Empty));
    }

    /// <summary>The same rules for a wire request, whose metric travels as its name.</summary>
    /// <param name="projectId">The project whose counter moves, or null for the tenant-level counter.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="newLimit">The new limit.</param>
    /// <param name="reason">Why the limit was raised.</param>
    internal static Result<QuotaLimitChange> Prepare(
        Guid? projectId,
        string? period,
        string? metric,
        decimal newLimit,
        string? reason)
    {
        if (!UsagePriceCardRules.TryParseMetric(metric ?? string.Empty, out var parsed))
        {
            return Result.Failure<QuotaLimitChange>(Error.Invalid(
                InvalidMessage,
                [new ErrorDetail("metric", "invalid_value")]));
        }

        return Prepare(new RaiseQuotaLimit(projectId, period, parsed, newLimit, reason));
    }

    private static void ValidatePeriod(string? period, ref List<ErrorDetail>? violations)
    {
        if (string.IsNullOrEmpty(period))
        {
            (violations ??= []).Add(new ErrorDetail("period", "required"));
        }
        else if (!UsagePeriod.IsQuotaPeriod(period))
        {
            (violations ??= []).Add(new ErrorDetail("period", "invalid_value"));
        }
    }

    private static void ValidateMetric(bool defined, ref List<ErrorDetail>? violations)
    {
        if (!defined)
        {
            (violations ??= []).Add(new ErrorDetail("metric", "invalid_value"));
        }
    }

    /// <summary>
    /// Checks the caller's reason and answers it trimmed. The text PostgreSQL keeps and the one-line rule are checked
    /// on the caller's own text <em>before</em> the trim, because trimming would silently accept a leading line break
    /// or a trailing bidi mark the rule refuses; the rune cap and the audit metadata value rule then apply to the
    /// trimmed value, which is what the row records. The first rule the reason breaks is the one detail the caller gets.
    /// </summary>
    /// <param name="reason">The caller's reason.</param>
    /// <param name="violations">The details collected so far.</param>
    /// <returns>The trimmed reason, or null when it is refused.</returns>
    private static string? NormalizeReason(string? reason, ref List<ErrorDetail>? violations)
    {
        if (string.IsNullOrEmpty(reason))
        {
            (violations ??= []).Add(new ErrorDetail("reason", "required"));
            return null;
        }

        if (!PostgresText.IsStorable(reason) || !PostgresText.IsPlain(reason))
        {
            (violations ??= []).Add(new ErrorDetail("reason", "invalid_value"));
            return null;
        }

        var trimmed = reason.Trim();
        if (trimmed.Length == 0)
        {
            (violations ??= []).Add(new ErrorDetail("reason", "required"));
            return null;
        }

        if (trimmed.EnumerateRunes().Count() > MaxReasonLength)
        {
            (violations ??= []).Add(new ErrorDetail("reason", "too_long"));
            return null;
        }

        var outcome = AuditMetadata.ValueOutcome(trimmed);
        if (outcome == AuditMetadata.MetadataValueOutcome.Accepted)
        {
            return trimmed;
        }

        (violations ??= []).Add(new ErrorDetail(
            "reason",
            outcome == AuditMetadata.MetadataValueOutcome.TooLong ? "too_long" : "invalid_value"));

        return null;
    }
}

/// <summary>
/// Applies <see cref="RaiseQuotaLimit"/> on the pipeline's tenant transaction and describes the audit row of
/// the change; the pipeline records that row with the caller and the commit boundary together.
/// </summary>
[AuditedAction("usage.quota_raise")]
[Permission(Permissions.Billing.Write)]
internal sealed class RaiseQuotaLimitHandler(
    QuotaLimitWriter writer,
    ITenantScopeAccessor scopes,
    IAuditTrail audit) : ICommandHandler<RaiseQuotaLimit, Result<QuotaRaiseResult>>
{
    /// <summary>Validates, raises the counter and describes the audit object of the raise.</summary>
    /// <param name="command">The raise.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<QuotaRaiseResult>> Handle(
        RaiseQuotaLimit command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Usage, nameof(RaiseQuotaLimit));

        var prepared = QuotaRaiseRules.Prepare(command);
        if (prepared.Error is { } error)
        {
            return Result.Failure<QuotaRaiseResult>(error);
        }

        var change = prepared.Value;
        var result = await writer
            .ApplyAsync(scopes.Require().OrgId, change, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result;
        }

        QuotaLimitWriter.Describe(audit, change, result.Value);
        activity?.SetTag("usage.quota.raised", 1);

        return result;
    }
}
