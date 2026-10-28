using System.Security.Claims;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The operator-ingress endpoints of 01.5.1 §5/T4.2 (00 §9.1): dead-letter
/// listing and replay live under <c>/internal/operator/*</c>, guarded by
/// <see cref="IOperatorGate"/> and excluded from the served OpenAPI document
/// (T16/CR-01-4) — the same posture <c>/internal/health/*</c> and
/// <c>/internal/slice</c> already take. No <c>/v1</c> mount exists: operator
/// routes carry no tenant context, so the tenant pipeline never applies (§5's
/// "routes are outside /v1").
/// </summary>
/// <remarks>
/// The handlers call the queue services directly rather than going through the
/// Mediator C2 pipeline: a Result-contract command would trip the
/// tenant-transaction stage's fail-closed Require() on a request that has no
/// org, and the operator surface is outside the tenant contract by design.
/// Replay is naturally idempotent through the row's <c>state = 'dead_letter'</c>
/// guard, so neither route needs an <c>Idempotency-Key</c>.
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

        // 08.6.3-T1 (AC1/T72): the platform-wide healing switch. The same gate and envelope filters as the task
        // routes, and the same ExcludeFromDescription posture: the operator surface is never in the served document.
        var healing = app.MapGroup("/internal/operator/healing")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        healing.MapPost("/switch", SetHealingSwitchSystemAsync);
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
    /// <c>GET /internal/operator/tasks/dead-letter?queue=&amp;cursor=&amp;limit=</c>
    /// → the keyset page of <c>task_dead_idx</c> (00 §9.1 ordering
    /// <c>(created_at, id)</c>, response <c>{ items, next_cursor }</c>).
    /// </summary>
    private static async Task<Result<DeadLetterTaskPage>> ListDeadLetterAsync(
        string? queue,
        string? cursor,
        string? limit,
        ListDeadLetterTasksQuery query,
        HttpContext context)
    {
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
    /// <c>POST /internal/operator/tasks/dead-letter/{id}/replay</c> →
    /// <c>ReplayDeadLetterTask</c>: the guarded <c>state = 'dead_letter'</c>
    /// update, mapped to 200 with the row's new state / 404
    /// <c>task_not_found</c> / 409 <c>task_not_dead_letter</c> (§5).
    /// </summary>
    private static async Task<Result<ReplayDeadLetterTaskResponse>> ReplayDeadLetterAsync(
        Guid id,
        ReplayDeadLetterTask replay,
        HttpContext context)
    {
        var outcome = await replay.ExecuteAsync(id, context.RequestAborted);

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
    /// platform-wide row, set through migration 0052's definer on a non-tenant connection (08.6.3 §5, T72). The
    /// principal's user id is the row's <c>set_by</c>; an unauthenticated caller is 401 and an authenticated one
    /// without a subject 403, so a system write can never be attributed to nobody. 422
    /// <c>invalid_switch_mode</c> / <c>mode_not_available</c> come from the same gate the tenant route uses.
    /// </summary>
    private static async Task<Result<HealingSwitchView>> SetHealingSwitchSystemAsync(
        HealingSystemSwitchRequest request,
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

        return await writer.SetAsync(request.Mode, request.Reason, setBy, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// The 200 body of the replay endpoint (01.5.1 §5, snake_case on the wire):
/// the row's id, its new <c>state</c>, the reset <c>attempts</c> and the
/// <c>available_at</c> the guarded update set.
/// </summary>
public sealed record ReplayDeadLetterTaskResponse(Guid Id, string State, int Attempts, DateTime AvailableAt);

/// <summary>
/// The body of <c>POST /internal/operator/healing/switch</c> (08.6.3 §5): the platform-wide mode and its reason.
/// <c>set_by</c> is never in the body — it is the authenticated operator's own id.
/// </summary>
/// <param name="Mode">One of <c>enabled</c>, <c>shadow</c>, <c>disabled</c>.</param>
/// <param name="Reason">Why the switch was set, 1–2000 characters when present.</param>
public sealed record HealingSystemSwitchRequest(string? Mode, string? Reason);
