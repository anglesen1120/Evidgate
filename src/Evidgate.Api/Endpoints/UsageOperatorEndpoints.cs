using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Usage.Application.Billing;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Application.Quota;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The billing operator's surface on the operator ingress: one org's audited ledger page, period reconciliation and
/// invoice worksheet, the audited quota raise and the statement it issues. All of them sit under
/// <c>/internal/operator/*</c> for the same reason the platform routes do — they serve the operator, not a tenant
/// caller — and all are excluded from the served OpenAPI document, the SDK and the permission catalogue, because the
/// operator surface is not a tenant contract.
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
    /// The most a write body may carry. Both write routes take a handful of scalars — the raise's reason is at most
    /// 200 characters — so this is far above any legal body while a hostile one costs no more than the cap.
    /// </summary>
    private const int MaxBodyBytes = 4 * 1024;

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
        usage.MapGet("/invoice-worksheet", GetInvoiceWorksheetAsync);

        // The write side of the billing operator's surface: one scope's limit, with a reason the audit row
        // keeps and the response of the counter it left behind; and the statement that freezes a closed
        // period's worksheet as the invoice the org is billed on.
        var billing = app.MapGroup("/internal/operator/billing/orgs/{orgId:guid}")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        billing.MapPost("/quota-raise", RaiseQuotaAsync);
        billing.MapPost("/statements", IssueStatementAsync);
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
    /// <c>GET /internal/operator/orgs/{o}/invoice-worksheet?period=</c> → the audited worksheet of one period: its
    /// usage rows, its corrections and the ledger's own totals over them, hashed so two reads can be compared.
    /// </summary>
    private static async Task<Result<InvoiceWorksheet>> GetInvoiceWorksheetAsync(
        Guid orgId,
        string? period,
        InvoiceWorksheetReader reader,
        HttpContext context)
    {
        if (!TryReadOperator(context, out var operatorId))
        {
            return Result.Failure<InvoiceWorksheet>(Unattributable);
        }

        return await reader
            .ExecuteAsync(orgId, operatorId, period, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /internal/operator/billing/orgs/{o}/statements</c> → the statement that freezes one closed period:
    /// <c>{period, total_amount, currency}</c> answered with the stored statement and whether this call replayed one.
    /// The submitted total is checked against the server's recompute, never trusted; a re-issue whose recompute
    /// still hashes to the issued document replays it, and one whose recompute moved is 409 <c>statement_exists</c>.
    /// <para>
    /// Like the raise, the body is read <em>inside</em> the handler after the gate admitted the caller, so a refused
    /// caller never learns from a 400 whether its body was malformed.
    /// </para>
    /// </summary>
    private static async Task<Result<IssuedStatement>> IssueStatementAsync(
        Guid orgId,
        StatementIssuer issuer,
        HttpContext context)
    {
        if (!TryReadOperator(context, out var operatorId))
        {
            return Result.Failure<IssuedStatement>(Unattributable);
        }

        var read = await ReadCappedBodyAsync<IssueStatementRequest>(context, "statement").ConfigureAwait(false);
        if (read.Error is { } refusal)
        {
            return Result.Failure<IssuedStatement>(refusal);
        }

        return await issuer
            .ExecuteAsync(orgId, operatorId, read.Value, context.RequestAborted)
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

        var read = await ReadCappedBodyAsync<QuotaRaiseRequest>(context, "quota raise").ConfigureAwait(false);
        if (read.Error is { } refusal)
        {
            return Result.Failure<QuotaRaiseResult>(refusal);
        }

        return await raiser
            .ExecuteAsync(orgId, operatorId, read.Value, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one write body under the route's cap. A declared <c>Content-Length</c> over it is refused before a
    /// byte is read, and a chunked or length-less body — which no header reveals — is counted while it is read,
    /// so an oversized body costs no parse and no statement.
    /// </summary>
    /// <typeparam name="T">The body's shape.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="what">The body's name, so a refusal names the route it belongs to.</param>
    /// <returns>The body, or the 413/400 the route answers.</returns>
    private static async Task<Result<T>> ReadCappedBodyAsync<T>(HttpContext context, string what)
        where T : class
    {
        if (context.Request.ContentLength is { } declared && declared > MaxBodyBytes)
        {
            return Result.Failure<T>(CappedRequestBody.TooLarge);
        }

        T? request;
        var body = context.Request.Body;
        try
        {
            context.Request.Body = new CappedBodyStream(body, MaxBodyBytes);
            request = await context.Request
                .ReadFromJsonAsync<T>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException tooLarge)
        {
            return Result.Failure<T>(CappedRequestBody.Unreadable(tooLarge));
        }
        catch (Exception unreadable) when (unreadable is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<T>(Error.BadRequest(
                $"the {what} body is not valid JSON",
                [new ErrorDetail("body", "invalid_json")]));
        }
        finally
        {
            context.Request.Body = body;
        }

        return request is null
            ? Result.Failure<T>(Error.BadRequest(
                $"the {what} body is required",
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
