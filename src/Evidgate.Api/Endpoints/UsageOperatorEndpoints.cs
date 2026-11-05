using System.Globalization;
using System.Security.Claims;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Queries;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The billing operator's two audited reads on the operator ingress: one org's ledger page and the
/// reconciliation of one period against its roll-up. Both sit under <c>/internal/operator/orgs/{o}</c>
/// for the same reason the platform routes do — they serve the operator, not a tenant caller — and
/// both are excluded from the served OpenAPI document, the SDK and the permission catalogue, because
/// the operator surface is not a tenant contract.
/// </summary>
/// <remarks>
/// The handlers call the module's read services directly rather than a Mediator query: a
/// <c>Result</c>-contract query carries the tenant pipeline's stages, and this surface has no tenant
/// principal to publish — the admitted caller is the platform operator, and the org is the route's
/// parameter. The <c>billing:read</c> semantic therefore rides on <see cref="IOperatorGate"/>, which
/// is what admits the request before any handler runs.
/// </remarks>
internal static class UsageOperatorEndpoints
{
    /// <summary>The denial of a gate that admitted a caller nobody can be held to.</summary>
    private static readonly Error Unattributable = Error.Forbidden(
        "the operator principal carries no user id, so the billing read cannot be attributed");

    /// <summary>Maps the billing operator's usage reads, excluded from the OpenAPI document.</summary>
    /// <param name="app">The host's application.</param>
    internal static void MapUsageOperatorEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The same two group filters as the platform routes, in the same order: the gate decides who
        // may read, then the envelope filter renders whatever the handler answered.
        var usage = app.MapGroup("/internal/operator/orgs/{orgId:guid}")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        usage.MapGet("/usage-ledger", ListUsageLedgerAsync);
        usage.MapGet("/usage-reconciliation", GetPeriodReconciliationAsync);
    }

    /// <summary>
    /// <c>GET /internal/operator/orgs/{o}/usage-ledger?period=&amp;metric=&amp;cursor=&amp;limit=</c>
    /// → one audited keyset page of the org's ledger rows.
    /// </summary>
    private static async Task<Result<UsageLedgerPage>> ListUsageLedgerAsync(
        Guid orgId,
        string? period,
        string? metric,
        string? cursor,
        string? limit,
        UsageLedgerReader reader,
        HttpContext context)
    {
        var pageSize = UsageLedgerReader.DefaultLimit;
        if (!string.IsNullOrEmpty(limit))
        {
            if (!int.TryParse(limit, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                return Result.Failure<UsageLedgerPage>(Error.BadRequest(
                    "limit must be an integer",
                    [new ErrorDetail("limit", "integer")]));
            }

            pageSize = parsed;
        }

        if (!TryReadOperator(context, out var operatorId))
        {
            return Result.Failure<UsageLedgerPage>(Unattributable);
        }

        return await reader
            .ExecuteAsync(orgId, operatorId, period, metric, cursor, pageSize, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /internal/operator/orgs/{o}/usage-reconciliation?period=</c> → the audited comparison of
    /// the period's ledger totals against the roll-up, with the roll-up's gaps and the duplicate attempts.
    /// </summary>
    private static async Task<Result<PeriodReconciliation>> GetPeriodReconciliationAsync(
        Guid orgId,
        string? period,
        PeriodReconciliationReader reader,
        HttpContext context)
    {
        if (!TryReadOperator(context, out var operatorId))
        {
            return Result.Failure<PeriodReconciliation>(Unattributable);
        }

        return await reader
            .ExecuteAsync(orgId, operatorId, period, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The operator's own id, which every audit row of these reads is attributed to. A gate that
    /// admits a caller without a subject would make an unattributable billing read possible, so that
    /// case is refused rather than written as the system.
    /// </summary>
    private static bool TryReadOperator(HttpContext context, out Guid operatorId)
    {
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        return Guid.TryParse(subject, out operatorId);
    }
}
