using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// Carries what a quota block decided inside the tenant transaction out to the endpoint that answers it: the
/// <c>Retry-After</c> the client is told, and the refused question the durable stamp needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>One slot per request.</b> The carrier is a singleton, so a plain field would let two concurrent requests
/// read each other's block: the slot is an <see cref="AsyncLocal{T}"/> the endpoint installs per request, and the
/// handler's write lands in the box that slot captured. The wait cannot ride the handler's <c>Result</c>
/// instead, because a failure's payload is the wire envelope and its details are the tenant-safe ones only.
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
    /// The question the refused batch asked and the tenant it ran as, or null when the batch was not refused for
    /// quota. The refusal's own stamp rolled back with the batch, so this is what the endpoint writes again.
    /// </summary>
    internal QuotaBlock? Block => _ambient.Value?.Block;

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
    /// Records the block a batch was refused by: the wait it is told, rounded up to whole seconds and never below one,
    /// so a client never reads <c>Retry-After: 0</c> as "retry now" (00 §9.2), and the refused question itself.
    /// </summary>
    /// <param name="orgId">The tenant the refused batch ran as.</param>
    /// <param name="request">The gate question the batch was refused by.</param>
    /// <param name="retryAfter">The gate's wait until the next billing period starts, when it named one.</param>
    internal void Refuse(Guid orgId, QuotaCheckRequest request, TimeSpan? retryAfter)
    {
        var slot = _ambient.Value ??= new Slot();

        if (retryAfter is { } wait)
        {
            slot.RetryAfterSeconds = (int)Math.Max(1, Math.Ceiling(wait.TotalSeconds));
        }

        slot.Block = new QuotaBlock(orgId, request);
    }

    /// <summary>The mutable box shared down the request's async flow (see the type's remarks).</summary>
    private sealed class Slot
    {
        internal int? RetryAfterSeconds { get; set; }

        internal QuotaBlock? Block { get; set; }
    }

    private sealed class RequestScope(IngestQuotaVeto owner, Slot? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => owner._ambient.Value = previous;
    }
}

/// <summary>One refused batch's gate question, and the tenant it ran as.</summary>
/// <param name="OrgId">The tenant the batch ran as: the scope the durable stamp opens.</param>
/// <param name="Request">The question the gate blocked, whose scopes the stamp names.</param>
internal sealed record QuotaBlock(Guid OrgId, QuotaCheckRequest Request);
