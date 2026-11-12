using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The operator-ingress endpoints: dead-letter
/// listing and replay live under <c>/internal/operator/*</c>, guarded by
/// <see cref="IOperatorGate"/> and excluded from the served OpenAPI document
/// — the same posture <c>/internal/health/*</c> and
/// <c>/internal/health</c> already take. No <c>/v1</c> mount exists: the operator
/// surface is not a tenant contract.
/// </summary>
/// <remarks>
/// The handlers call the queue services directly rather than going through the
/// Mediator C2 pipeline, because that pipeline's tenant stage is entered by a tenant
/// principal and this surface is admitted by the operator gate. Each route names its org
/// as a required <c>org_id</c> parameter, and the queue service opens that org's tenant
/// transaction around the owner <c>SECURITY DEFINER</c> function, which reads the org from
/// <c>evidgate.current_org_id()</c> rather than from a caller-supplied filter. Replay is
/// naturally idempotent through the row's <c>state = 'dead_letter'</c> guard, so neither
/// route needs an <c>Idempotency-Key</c>.
/// </remarks>
public static class OperatorEndpoints
{
    /// <summary>Maps the operator endpoints on the operator ingress, excluded from the OpenAPI document.</summary>
    public static void MapOperatorEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Group-level: RequireOperatorFilter runs first, then ResultEndpointFilter
        // renders the outcome. ExcludeFromDescription on the group (mirroring the
        // InternalEndpoints posture) keeps every route off the served document.
        var group = app.MapGroup("/internal/operator/tasks")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        group.MapGet("/dead-letter", ListDeadLetterAsync);
        group.MapPost("/dead-letter/{id:guid}/replay", ReplayDeadLetterAsync);

        // The platform-wide healing switch. The same gate and envelope filters as the task
        // routes, and the same ExcludeFromDescription posture: the operator surface is never in the served document.
        var healing = app.MapGroup("/internal/operator/healing")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        healing.MapPost("/switch", SetHealingSwitchSystemAsync);

        // The billing operator's audited reads (the invoice statement and its reconciliation). Same gate and
        // envelope, same exclusion from the served document; the routes own the org segment because an operator read
        // is not scoped to one tenant by the request pipeline.
        app.MapUsageOperatorEndpoints();
    }

    /// <summary>
    /// Registers the operator surface: the two task services plus the
    /// fail-closed <see cref="IOperatorGate"/> default a real host composition
    /// must replace (the gate that actually checks the platform-operator
    /// credential lands with the 02.x authentication story).
    /// </summary>
    public static IServiceCollection AddOperatorEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOperatorGate, DenyAllOperatorGate>();
        services.AddSingleton<ListDeadLetterTasksQuery>();
        services.AddSingleton<ReplayDeadLetterTask>();

        return services;
    }

    /// <summary>
    /// <c>GET /internal/operator/tasks/dead-letter?org_id=&amp;queue=&amp;cursor=&amp;limit=</c>
    /// → the keyset page of one org's dead-lettered rows (ordering
    /// <c>(created_at, id)</c>, response <c>{ items, next_cursor }</c>).
    /// </summary>
    private static async Task<Result<DeadLetterTaskPage>> ListDeadLetterAsync(
        Guid? org_id,
        string? queue,
        string? cursor,
        string? limit,
        ListDeadLetterTasksQuery query,
        HttpContext context)
    {
        if (org_id is not { } orgId)
        {
            return Result.Failure<DeadLetterTaskPage>(Error.BadRequest(
                "org_id is required: an operator read of the dead-letter queue is scoped to one organization",
                [new ErrorDetail("org_id", "required")]));
        }

        var pageSize = ListDeadLetterTasksQuery.DefaultLimit;
        if (!string.IsNullOrEmpty(limit))
        {
            if (!int.TryParse(limit, out var parsed))
            {
                return Result.Failure<DeadLetterTaskPage>(Error.BadRequest(
                    "limit must be an integer",
                    [new ErrorDetail("limit", "integer")]));
            }

            pageSize = parsed;
        }

        DeadLetterTaskPage page;
        try
        {
            // An empty queue parameter means "no filter", not "match a queue
            // named ''" — a client rendering an unset filter must not see an
            // empty page while rows exist (QA-H defect H-3).
            page = await query.ExecuteAsync(
                orgId,
                string.IsNullOrEmpty(queue) ? null : queue,
                cursor,
                pageSize,
                context.RequestAborted);
        }
        catch (FormatException)
        {
            return Result.Failure<DeadLetterTaskPage>(Error.BadRequest(
                "the cursor is not a dead-letter continuation this endpoint produced",
                [new ErrorDetail("cursor", "invalid")]));
        }

        return Result.Success(page);
    }

    /// <summary>
    /// <c>POST /internal/operator/tasks/dead-letter/{id}/replay?org_id={o}</c> →
    /// <c>ReplayDeadLetterTask</c>: the guarded <c>state = 'dead_letter'</c>
    /// transition, mapped to 200 with the row's new state / 404
    /// <c>task_not_found</c> / 409 <c>task_not_dead_letter</c>. The acting
    /// operator is the audit row's actor, so a principal nobody can be held to is refused.
    /// </summary>
    private static async Task<Result<ReplayDeadLetterTaskResponse>> ReplayDeadLetterAsync(
        Guid id,
        Guid? org_id,
        ReplayDeadLetterTask replay,
        HttpContext context)
    {
        if (org_id is not { } orgId)
        {
            return Result.Failure<ReplayDeadLetterTaskResponse>(Error.BadRequest(
                "org_id is required: a replay is scoped to the organization that owns the task",
                [new ErrorDetail("org_id", "required")]));
        }

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Result.Failure<ReplayDeadLetterTaskResponse>(new Error(
                ApiErrorCode.Unauthenticated,
                "the operator principal is not authenticated",
                StatusCodes.Status401Unauthorized));
        }

        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var actor))
        {
            return Result.Failure<ReplayDeadLetterTaskResponse>(Error.Forbidden(
                "the operator principal carries no user id, so the replay cannot be attributed"));
        }

        var outcome = await replay.ExecuteAsync(orgId, id, actor, context.RequestAborted);

        return outcome.Outcome switch
        {
            ReplayDeadLetterOutcome.Replayed => Result.Success(
                new ReplayDeadLetterTaskResponse(id, "queued", 0, outcome.AvailableAt!.Value)),
            ReplayDeadLetterOutcome.NotFound => Result.Failure<ReplayDeadLetterTaskResponse>(
                new Error(ApiErrorCode.TaskNotFound, "no task with that id exists", 404)),
            _ => Result.Failure<ReplayDeadLetterTaskResponse>(Error.Conflict(
                ApiErrorCode.TaskNotDeadLetter,
                "the task is not dead-lettered")),
        };
    }

    /// <summary>
    /// <c>POST /internal/operator/healing/switch</c> → <see cref="HealingSystemSwitchWriter"/>: the
    /// platform-wide row, set through migration 0052's definer on a non-tenant connection. The
    /// principal's user id is the row's <c>set_by</c>; an unauthenticated caller is 401 and an authenticated one
    /// without a subject 403, so a system write can never be attributed to nobody. 422
    /// <c>invalid_switch_mode</c> / <c>mode_not_available</c> come from the same gate the tenant route uses.
    /// <para>
    /// The body is read <em>inside</em> the handler, after those two checks, instead of being bound as a parameter:
    /// a bound parameter is materialised by the framework before this method runs, so an anonymous caller could
    /// learn from a 400 that its body was malformed — and a body is untrusted input the gate has not yet admitted.
    /// An unreadable or absent body is 400 <c>request_invalid</c> here, once the caller is authenticated and
    /// attributable.
    /// </para>
    /// <para>
    /// There is deliberately no <c>Idempotency-Key</c> on this route (D-S124-Q4): <c>set</c> is naturally idempotent
    /// on the mode — a retry writes the same value and only bumps <c>version</c> — and the operator surface has a
    /// single trusted caller, not a client fleet retrying on timeouts.
    /// </para>
    /// </summary>
    private static async Task<Result<HealingSwitchView>> SetHealingSwitchSystemAsync(
        HttpContext context,
        HealingSystemSwitchWriter writer)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Result.Failure<HealingSwitchView>(new Error(
                ApiErrorCode.Unauthenticated,
                "the operator principal is not authenticated",
                StatusCodes.Status401Unauthorized));
        }

        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var setBy))
        {
            return Result.Failure<HealingSwitchView>(Error.Forbidden(
                "the operator principal carries no user id, so the switch change cannot be attributed"));
        }

        HealingSystemSwitchRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<HealingSystemSwitchRequest>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Result.Failure<HealingSwitchView>(Error.BadRequest(
                "the switch request body is not valid JSON",
                [new ErrorDetail("body", "invalid_json")]));
        }

        if (request is null)
        {
            return Result.Failure<HealingSwitchView>(Error.BadRequest(
                "the switch request body is required",
                [new ErrorDetail("body", "required")]));
        }

        return await writer.SetAsync(request.Mode, request.Reason, setBy, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// The 200 body of the replay endpoint (snake_case on the wire):
/// the row's id, its new <c>state</c>, the reset <c>attempts</c> and the
/// <c>available_at</c> the guarded update set.
/// </summary>
public sealed record ReplayDeadLetterTaskResponse(Guid Id, string State, int Attempts, DateTime AvailableAt);

/// <summary>
/// The body of <c>POST /internal/operator/healing/switch</c>: the platform-wide mode and its reason.
/// <c>set_by</c> is never in the body — it is the authenticated operator's own id.
/// </summary>
/// <param name="Mode">One of <c>enabled</c>, <c>shadow</c>, <c>disabled</c>.</param>
/// <param name="Reason">Why the switch was set, 1–2000 characters when present.</param>
public sealed record HealingSystemSwitchRequest(string? Mode, string? Reason);
