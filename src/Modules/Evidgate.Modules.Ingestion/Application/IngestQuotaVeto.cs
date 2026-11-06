namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// Carries the quota block's <c>Retry-After</c> from the command that decided it to the endpoint that answers
/// with it: the gate's wait is known only inside the tenant transaction, while the header belongs to the response
/// the endpoint writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient, not scoped.</b> Mediator resolves handlers from the root provider, so a scoped service would be
/// a root singleton in production (one request's wait leaking into another) and a scope-validation failure in
/// the hosts' tests. The seam therefore has the shape of the pipeline's ambient caller and tenant: a singleton
/// whose slot is an <see cref="AsyncLocal{T}"/>, installed by the endpoint per request.
/// </para>
/// <para>
/// An <see cref="AsyncLocal{T}"/> write inside an awaited callee does not surface in the caller's execution
/// context, so the endpoint calls <see cref="BeginRequest"/> first: the box it installs is captured down into
/// the handler's flow by reference, and the handler's write is what the endpoint then reads. A request the gate
/// admitted sets nothing, so a stale wait can never turn a 200 into a header value.
/// </para>
/// </remarks>
internal sealed class IngestQuotaVeto
{
    private readonly AsyncLocal<Slot?> _ambient = new();

    /// <summary>The block's <c>Retry-After</c> in whole seconds, or null when the batch was not refused for quota.</summary>
    internal int? RetryAfterSeconds => _ambient.Value?.RetryAfterSeconds;

    /// <summary>
    /// Installs a fresh, empty slot for the request the endpoint is serving and restores the previous one on
    /// disposal, so two requests that share a thread — or a test that reuses a scope — never read each other's wait.
    /// </summary>
    /// <returns>The scope that restores the enclosing slot.</returns>
    internal IDisposable BeginRequest()
    {
        var previous = _ambient.Value;
        _ambient.Value = new Slot();

        return new RequestScope(this, previous);
    }

    /// <summary>
    /// Records the wait a blocked batch is told: rounded up to whole seconds and never below one, so a client
    /// never reads <c>Retry-After: 0</c> as "retry now" (00 §9.2).
    /// </summary>
    /// <param name="retryAfter">The gate's wait until the next billing period starts.</param>
    internal void Refuse(TimeSpan retryAfter)
    {
        var slot = _ambient.Value ??= new Slot();
        slot.RetryAfterSeconds = (int)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));
    }

    /// <summary>The mutable box shared down the request's async flow (see the type's remarks).</summary>
    private sealed class Slot
    {
        internal int? RetryAfterSeconds { get; set; }
    }

    private sealed class RequestScope(IngestQuotaVeto owner, Slot? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => owner._ambient.Value = previous;
    }
}
