using System.Diagnostics.CodeAnalysis;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// A consumer's handler for one <c>task_type</c> (01.5.1 §5). A handler is
/// registered by its own type and needs no external registration call: the registry
/// takes everything the container knows.
/// </summary>
public interface ITaskHandler
{
    /// <summary>The <c>task_type</c> this handler consumes; unique per process.</summary>
    string TaskType { get; }

    /// <summary>
    /// Whether the handler works on one tenant's rows. The default is true, so a
    /// handler is tenant-bound unless it says otherwise: a claimed row without an
    /// <c>org_id</c> is then dead-lettered as <c>missing_tenant</c> instead of being
    /// run unscoped (02.6.2 AC3). Only the mechanism's own cross-org sweeps override
    /// this with false.
    /// </summary>
    bool RequiresTenant => true;
}

/// <summary>
/// A typed handler: the consumer binds the row's payload to
/// <typeparamref name="TPayload"/> and runs the handler with the tenant scope it
/// opened from <c>task.org_id</c>.
/// <para>
/// A worker may run the same row twice (at-least-once, A10 — a crash between the
/// handler's commit and the completion, or a lease that ran out), so the handler
/// must be idempotent on the work's natural key; that key is what the producer put
/// in <c>task.idempotency_key</c>.
/// </para>
/// </summary>
/// <typeparam name="TPayload">Type the task payload JSON binds to (snake_case fields, 00 §7).</typeparam>
public interface ITaskHandler<TPayload> : ITaskHandler
{
    /// <summary>
    /// Runs one attempt.
    /// </summary>
    /// <param name="payload">The row's payload.</param>
    /// <param name="scope">
    /// The tenant scope opened from the row's <c>org_id</c>, or null for a platform
    /// task (<c>org_id</c> is null — the mechanism's own sweeps run without a
    /// tenant, 01.5.1 §3). A tenant handler's statements must run inside the scope:
    /// the scope is the only place the tenant setting exists, and row level
    /// security is what makes the boundary real (00 §5.4).
    /// </param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    /// <remarks>
    /// Returning means success: the consumer commits the scope and marks the row
    /// `done`. Throwing means the attempt failed and the row moves to `retry` (or
    /// `dead_letter` at the cap) with the scope rolled back. The handler must not
    /// commit the scope itself and must not catch-and-swallow.
    /// <para>
    /// <see cref="Exception.Message"/>'s first line is stored verbatim in
    /// <c>task.last_error</c> (00 §5.7 S3: category + message head) and reaches the
    /// operator's dead-letter list (01.5.1-T4.2) and the consumer's logs. A handler
    /// must never let a connection string, credential or request body reach the
    /// first line of a thrown exception's message.
    /// </para>
    /// </remarks>
    ValueTask HandleAsync(TPayload payload, ITenantScope? scope, CancellationToken cancellationToken);
}

/// <summary>
/// The dispatch seam between the consumer and a handler: binds a payload string,
/// then invokes. One adapter per registered handler.
/// </summary>
public interface ITaskHandlerInvoker
{
    /// <summary>The <c>task_type</c> this invoker dispatches.</summary>
    string TaskType { get; }

    /// <summary>Whether the wrapped handler needs a tenant scope (see <see cref="ITaskHandler.RequiresTenant"/>).</summary>
    bool RequiresTenant { get; }

    /// <summary>Binds <paramref name="payloadJson"/> to the handler's payload type and runs it.</summary>
    ValueTask InvokeAsync(string payloadJson, ITenantScope? scope, CancellationToken cancellationToken);
}

/// <summary>Handler lookup by <c>task_type</c> (01.5.1-T4.1: registry dispatch).</summary>
public interface ITaskHandlerRegistry
{
    /// <summary>Every <c>task_type</c> this process can run; the claim filters on exactly this set.</summary>
    IReadOnlyCollection<string> TaskTypes { get; }

    /// <summary>Finds the handler for <paramref name="taskType"/>.</summary>
    bool TryGet(string taskType, [NotNullWhen(true)] out ITaskHandlerInvoker? invoker);
}
