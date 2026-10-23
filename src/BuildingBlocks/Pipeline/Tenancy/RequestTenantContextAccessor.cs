namespace Evidgate.BuildingBlocks.Pipeline.Tenancy;

/// <summary>
/// AsyncLocal-backed singleton implementing the ambient request tenant (00 §5.5
/// C2). Same shape as <c>NpgsqlTenantScopeFactory</c> — the ambient slot lives on
/// the singleton, so a context set in one async flow never leaks into another, and
/// the write is synchronous so it is visible to the caller's own continuations.
/// </summary>
public sealed class RequestTenantContextAccessor : IRequestTenantContextAccessor, IRequestTenantContextSetter
{
    private readonly AsyncLocal<IRequestTenantContext?> _ambient = new();

    /// <inheritdoc />
    public IRequestTenantContext? Current => _ambient.Value;

    /// <inheritdoc />
    public IRequestTenantContext Require() =>
        Current ?? throw new TenantContextMissingException();

    /// <inheritdoc />
    public IDisposable Publish(Guid orgId, Guid? projectId)
    {
        var previous = _ambient.Value;
        _ambient.Value = new RequestTenantContext(orgId, projectId);
        return new ContextScope(this, previous);
    }

    /// <summary>Restores the enclosing flow's context when the host's request scope ends.</summary>
    private sealed class ContextScope(RequestTenantContextAccessor owner, IRequestTenantContext? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => owner._ambient.Value = previous;
    }
}
