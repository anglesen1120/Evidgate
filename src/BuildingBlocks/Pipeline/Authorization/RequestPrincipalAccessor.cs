namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>
/// The ambient caller of the current async flow — the principal-side mirror of
/// <c>IRequestTenantContextAccessor</c>, and ambient for the same reason: the C2
/// behaviours are singletons under Mediator's default lifetime, so a scoped
/// service could never be injected into them.
/// <para>
/// Fail-closed by construction: <see cref="Current"/> is null when no entry point
/// resolved a caller, and the C2 authorization stage denies null rather than
/// guessing a principal. Nothing downstream of the principal resolver may pick a
/// caller, exactly as nothing may pick a tenant (00 §5.5 C2).
/// </para>
/// </summary>
public interface IRequestPrincipalAccessor
{
    /// <summary>The caller of the current async flow, or null when the host resolved none.</summary>
    IRequestPrincipal? Current { get; }
}

/// <summary>
/// The entry point's write half: the Api's credential resolution (02.6.2-T1.2) —
/// or a worker's task claim — publishes the caller once for the flow, and the
/// returned scope restores the previous value on disposal.
/// </summary>
public interface IRequestPrincipalSetter
{
    /// <summary>Publishes <paramref name="principal"/> for the current async flow.</summary>
    IDisposable Publish(IRequestPrincipal principal);
}

/// <summary>
/// AsyncLocal-backed singleton implementing the ambient caller. Same shape as
/// <c>RequestTenantContextAccessor</c>: the slot lives on the singleton, so a
/// principal set in one async flow never leaks into another, and the write is
/// synchronous so it is visible to the caller's own continuations.
/// </summary>
public sealed class RequestPrincipalAccessor : IRequestPrincipalAccessor, IRequestPrincipalSetter
{
    private readonly AsyncLocal<IRequestPrincipal?> _ambient = new();

    /// <inheritdoc />
    public IRequestPrincipal? Current => _ambient.Value;

    /// <inheritdoc />
    public IDisposable Publish(IRequestPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var previous = _ambient.Value;
        _ambient.Value = principal;
        return new PrincipalScope(this, previous);
    }

    /// <summary>Restores the enclosing flow's principal when the host's request scope ends.</summary>
    private sealed class PrincipalScope(RequestPrincipalAccessor owner, IRequestPrincipal? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => owner._ambient.Value = previous;
    }
}
