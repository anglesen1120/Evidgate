using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login.Completion;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// The explicit second-provider link's completion (02.3.1-T2.2, Q4): the shared callback has verified the
/// identity of a provider the signed-in user is adding, and this seam stages it for the confirmation instead
/// of writing anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is written here.</b> AC6 allows a second provider to reach an existing account only after an
/// explicit confirm, so the callback's job is to prove the browser still holds the session of the account the
/// link was started for and to leave the verified identity in the encrypted
/// <see cref="LinkConfirmCookie"/>; <c>POST /v1/auth/link/confirm</c> is the only caller of
/// <see cref="ILinkIdentityStore"/>, and it is what decides <c>linked</c>, <c>already_linked</c>, <c>moved</c>
/// or the 409 that refuses to take an identity from an account still in use.
/// </para>
/// <para>
/// <b>Same user or nothing.</b> The session cookie is resolved through <see cref="ISessionUserReader"/> and
/// must name exactly <see cref="LoginCompletionRequest.LinkUserId"/> — the account the state cookie was
/// issued for. A missing session, another user's session, or a session that expired or was revoked during the
/// provider round-trip answers 401 <c>unauthenticated</c> and stages no cookie: an attempt completed in another
/// browser, or after a sign-out, buys nothing. A database fault answers 503 <c>unavailable</c> with
/// <c>Retry-After</c>, and the callback has already deleted the pending-login cookie and spent its `state`, so
/// the answer means "start the link again".
/// </para>
/// <para>
/// The returned target is the allowlisted <c>returnUrl</c>, percent-encoded exactly as the login completion's
/// (<see cref="ReturnUrlEncoder"/>), so a link attempt cannot drift from a sign-in in where the browser lands.
/// </para>
/// </remarks>
/// <param name="sessions">The read-only session lookup the pre-tenant routes use.</param>
/// <param name="cookie">The staged-link cookie written on success.</param>
/// <param name="options">The bound login options (<c>Retry-After</c> of the 503).</param>
internal sealed class LinkCompletion(
    ISessionUserReader sessions,
    LinkConfirmCookie cookie,
    IOptions<LoginOptions> options) : ILinkCompletion
{
    /// <inheritdoc />
    public async ValueTask<Result<string>> CompleteAsync(
        LoginCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The callback routes a state of purpose `link` here and nowhere else, so an absent target is a
        // composition bug rather than a client's request: fail closed the same way an absent seam does.
        if (request.LinkUserId is not { } linkUserId)
        {
            return Fail(LinkErrors.UnavailableWithRetryAfter(request.HttpContext, options.Value));
        }

        var resolved = await sessions
            .ResolveAsync(request.HttpContext.Request.Cookies[SessionCookie.Name], cancellationToken)
            .ConfigureAwait(false);

        if (!resolved.IsSuccess)
        {
            return Fail(resolved.Error!.Status == StatusCodes.Status503ServiceUnavailable
                ? LinkErrors.UnavailableWithRetryAfter(request.HttpContext, options.Value)
                : resolved.Error!);
        }

        if (resolved.Value != linkUserId)
        {
            // A link attempt completed in another browser, or after a sign-out and a sign-in as someone
            // else: the verified identity is not staged for ANY account, and nothing is written.
            return Result.Failure<string>(LinkErrors.Unauthenticated);
        }

        cookie.Write(request.HttpContext, linkUserId, request.Identity);

        return Result.Success(ReturnUrlEncoder.ToLocation(request.ReturnUrl));
    }

    /// <summary>
    /// <paramref name="error"/> as the seam's failure. The 503 already carries <c>Retry-After</c> (set by
    /// <see cref="LinkErrors.UnavailableWithRetryAfter(Microsoft.AspNetCore.Http.HttpContext, LoginOptions)"/>), and it means
    /// "start the link again": the callback has already deleted the pending-login cookie and spent its
    /// one-time <c>state</c>, so replaying the same callback answers 401 <c>token_invalid</c>.
    /// </summary>
    /// <param name="error">The failure the callback renders.</param>
    private static Result<string> Fail(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return Result.Failure<string>(error);
    }
}
