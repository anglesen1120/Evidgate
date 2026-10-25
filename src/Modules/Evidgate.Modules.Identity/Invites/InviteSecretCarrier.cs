namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// Carries the plaintext invite token from the command that minted it to the endpoint that answers
/// with it (02.1.2-T2 §5, [DECISION D-S78-Q1]): the command's <c>Result&lt;MembershipView&gt;</c>
/// stays secret-free, so the response-replay store (<c>IdempotencyBehaviour</c> persists the
/// response body) can never hold a secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient, not scoped.</b> Mediator resolves handlers from the root provider, so a scoped
/// service would be a root singleton in production (one token leaking across concurrent requests)
/// and a scope-validation failure in the host's tests. This seam therefore has the same shape as the
/// pipeline's ambient caller and tenant (<c>RequestPrincipalAccessor</c>): a singleton whose slot is
/// an <see cref="AsyncLocal{T}"/>.
/// </para>
/// <para>
/// <b>Why a publisher.</b> An <see cref="AsyncLocal{T}"/> write inside an awaited callee does not
/// surface in the caller's execution context, so the endpoint calls <see cref="BeginRequest"/> first:
/// the box it installs is captured down into the handler's flow by reference, and the handler's write
/// (<see cref="Token"/>) is what the endpoint then reads and clears with <see cref="Take"/>. A request
/// that never mints anything sets nothing, and nothing is ever logged.
/// </para>
/// </remarks>
internal sealed class InviteSecretCarrier
{
    private readonly AsyncLocal<Slot?> _ambient = new();

    /// <summary>
    /// The invite token minted for this request, or null when nothing was minted (a refusal, or an
    /// <c>Idempotency-Key</c> replay, which answers the stored body without a token by design).
    /// </summary>
    public string? Token
    {
        get => _ambient.Value?.Token;
        set
        {
            var slot = _ambient.Value ??= new Slot();
            slot.Token = value;
        }
    }

    /// <summary>
    /// Installs a fresh, empty slot for the request the endpoint is serving and restores the previous
    /// one on disposal, so two requests that share a thread — or a test that reuses a scope — can
    /// never read each other's token.
    /// </summary>
    /// <returns>The scope that restores the enclosing slot.</returns>
    public IDisposable BeginRequest()
    {
        var previous = _ambient.Value;
        _ambient.Value = new Slot();

        return new RequestScope(this, previous);
    }

    /// <summary>Reads the token and clears the slot: an answer is given out once.</summary>
    public string? Take()
    {
        var slot = _ambient.Value;
        var token = slot?.Token;
        if (slot is not null)
        {
            slot.Token = null;
        }

        return token;
    }

    /// <summary>The mutable box shared down the request's async flow (see the type's remarks).</summary>
    private sealed class Slot
    {
        public string? Token { get; set; }
    }

    private sealed class RequestScope(InviteSecretCarrier owner, Slot? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => owner._ambient.Value = previous;
    }
}
