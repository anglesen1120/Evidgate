using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Application.Quota;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The billing operator's surface on the operator ingress: one org's audited ledger page and period
/// reconciliation, and the audited quota raise. All of them sit under <c>/internal/operator/*</c> for the same
/// reason the platform routes do — they serve the operator, not a tenant caller — and all are excluded from the
/// served OpenAPI document, the SDK and the permission catalogue, because the operator surface is not a tenant
/// contract.
/// </summary>
/// <remarks>
/// The handlers call the module's services directly rather than through the Mediator pipeline: a
/// <c>Result</c>-contract message carries the tenant pipeline's stages, and this surface has no tenant principal
/// to publish — the admitted caller is the platform operator, and the org is the route's parameter. The
/// <c>billing:read</c>/<c>billing:write</c> semantics therefore ride on <see cref="IOperatorGate"/>, which is
/// what admits the request before any handler runs.
/// </remarks>
internal static class UsageOperatorEndpoints
{
    /// <summary>The denial of a gate that admitted a caller nobody can be held to.</summary>
    private static readonly Error Unattributable = Error.Forbidden(
        "the operator principal carries no user id, so the billing action cannot be attributed");

    /// <summary>
    /// The most a raise body may carry. Its five members are scalars and the reason is at most 200 characters, so
    /// this is far above any legal body while a hostile one costs no more than the cap.
    /// </summary>
    private const int MaxRaiseBodyBytes = 4 * 1024;

    /// <summary>Maps the billing operator's routes, excluded from the OpenAPI document.</summary>
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

        // The write side of the billing operator's surface: one scope's limit, with a reason the audit row
        // keeps and the response of the counter it left behind.
        var billing = app.MapGroup("/internal/operator/billing/orgs/{orgId:guid}")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        billing.MapPost("/quota-raise", RaiseQuotaAsync);
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
    /// <c>POST /internal/operator/billing/orgs/{o}/quota-raise</c> → the audited raise of one scope's limit:
    /// <c>{project_id, period, metric, new_limit, reason}</c> answered with the counter the statement left
    /// behind. A limit at or below the stored one is 422 <c>request_invalid</c>, never a silent lowering; an
    /// unknown project and another tenant's project are the same 404 <c>not_found</c>.
    /// <para>
    /// The body is read <em>inside</em> the handler, after the gate admitted the caller and after the operator's
    /// own id was read, instead of being bound as a parameter: a bound parameter is materialised by the framework
    /// before the gate runs, so an anonymous caller could learn from a 400 that its body was malformed, and a body
    /// no gate has admitted would already be parsed. Every failure is the 00 §9.2 envelope.
    /// </para>
    /// </summary>
    private static async Task<Result<QuotaRaiseResult>> RaiseQuotaAsync(
        Guid orgId,
        QuotaLimitRaiser raiser,
        HttpContext context)
    {
        if (!TryReadOperator(context, out var operatorId))
        {
            return Result.Failure<QuotaRaiseResult>(Unattributable);
        }

        var read = await ReadRaiseAsync(context).ConfigureAwait(false);
        if (read.Error is { } refusal)
        {
            return Result.Failure<QuotaRaiseResult>(refusal);
        }

        return await raiser
            .ExecuteAsync(orgId, operatorId, read.Value, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the raise body under the route's cap. A declared <c>Content-Length</c> over it is refused before a
    /// byte is read, and a chunked or length-less body — which no header reveals — is counted while it is read,
    /// so an oversized body costs no parse and no statement.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The body, or the 413/400 the route answers.</returns>
    private static async Task<Result<QuotaRaiseRequest>> ReadRaiseAsync(HttpContext context)
    {
        if (context.Request.ContentLength is { } declared && declared > MaxRaiseBodyBytes)
        {
            return Result.Failure<QuotaRaiseRequest>(CappedRequestBody.TooLarge);
        }

        QuotaRaiseRequest? request;
        var body = context.Request.Body;
        try
        {
            context.Request.Body = new CappedBodyStream(body, MaxRaiseBodyBytes);
            request = await context.Request
                .ReadFromJsonAsync<QuotaRaiseRequest>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException tooLarge)
        {
            return Result.Failure<QuotaRaiseRequest>(CappedRequestBody.Unreadable(tooLarge));
        }
        catch (Exception unreadable) when (unreadable is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<QuotaRaiseRequest>(Error.BadRequest(
                "the quota raise body is not valid JSON",
                [new ErrorDetail("body", "invalid_json")]));
        }
        finally
        {
            context.Request.Body = body;
        }

        return request is null
            ? Result.Failure<QuotaRaiseRequest>(Error.BadRequest(
                "the quota raise body is required",
                [new ErrorDetail("body", "required")]))
            : Result.Success(request);
    }

    /// <summary>
    /// The operator's own id, which every audit row of these routes is attributed to. A gate that
    /// admits a caller without a subject would make an unattributable billing action possible, so that
    /// case is refused rather than written as the system.
    /// </summary>
    private static bool TryReadOperator(HttpContext context, out Guid operatorId)
    {
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        return Guid.TryParse(subject, out operatorId);
    }
}
