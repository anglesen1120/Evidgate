using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Usage.Application.Commands;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// The body of <c>POST /internal/operator/billing/orgs/{o}/quota-raise</c>: the scope the limit applies to,
/// its new value and the operator's reason.
/// </summary>
/// <param name="ProjectId">The project whose counter moves, or null for the tenant-level counter.</param>
/// <param name="Period">The billing month, <c>YYYY-MM</c>.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="NewLimit">The new limit; it must exceed the stored one.</param>
/// <param name="Reason">Why the limit was raised, 1..200 characters.</param>
internal sealed record QuotaRaiseRequest(
    Guid? ProjectId,
    string? Period,
    string? Metric,
    decimal NewLimit,
    string? Reason);

/// <summary>
/// The operator ingress path of a quota raise. The route names the org and the operator principal names the
/// actor, so this service opens the org's own scope, applies the raise through <see cref="QuotaLimitWriter"/>
/// and commits the counter together with its audit row.
/// <para>
/// It exists beside <see cref="RaiseQuotaLimit"/> rather than through the Mediator pipeline for the reason the
/// billing reads do: the operator surface has no tenant principal to publish, and the org is the route's
/// parameter. Both entry points share one writer, so the statement and the audit row have one definition.
/// </para>
/// </summary>
internal sealed class QuotaLimitRaiser(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ITenantLimits limits,
    QuotaLimitWriter writer)
{
    /// <summary>Validates the request, raises the counter and writes the audit row of the raise.</summary>
    /// <param name="orgId">The org the route names.</param>
    /// <param name="operatorId">The authenticated operator, the audit row's actor.</param>
    /// <param name="request">The body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<QuotaRaiseResult>> ExecuteAsync(
        Guid orgId,
        Guid operatorId,
        QuotaRaiseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = QuotaRaiseRules.Prepare(
            request.ProjectId, request.Period, request.Metric, request.NewLimit, request.Reason);
        if (prepared.Error is { } invalid)
        {
            return Result.Failure<QuotaRaiseResult>(invalid);
        }

        using var unit = audit.BeginUnit();
        await using var scope = await scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        if (await limits.ForOrganizationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<QuotaRaiseResult>(Error.NotFound("no such organization"));
        }

        var result = await writer
            .ApplyAsync(orgId, prepared.Value, cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is { } refusal)
        {
            return Result.Failure<QuotaRaiseResult>(refusal);
        }

        QuotaLimitWriter.Describe(audit, prepared.Value, result.Value);
        audit.Record(
            QuotaLimitWriter.Action,
            new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));

        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }
}
