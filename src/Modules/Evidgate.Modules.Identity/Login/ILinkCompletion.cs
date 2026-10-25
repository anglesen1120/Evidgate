using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The hand-over of an EXPLICIT second-provider LINK (02.3.1-T2, AC6, Q4): the callback has verified the
/// identity of a provider the signed-in user is adding to an account they already hold; what happens next —
/// moving the identity from another user when that user holds no membership and no live session, or the 409
/// that refuses it — belongs to this seam and never to the login completion.
/// </summary>
/// <remarks>
/// <para>
/// An explicit link is never a merge: the callback that carries a state cookie of purpose <c>link</c>
/// (<see cref="LoginStatePayload.PurposeLink"/>) is answered by THIS seam only, and by nothing at all when no
/// link seam is composed. It never falls back to <see cref="ILoginCompletion"/>: a link that quietly became a
/// sign-in would bind a second provider without the confirm the story requires.
/// </para>
/// <para>
/// The implementation is registered by the linking slice (02.3.1-T2.2) as a singleton, like the login
/// completion; a test substitutes it with one. It receives the same
/// <see cref="LoginCompletionRequest"/> shape, with <see cref="LoginCompletionRequest.LinkUserId"/> naming the
/// account the link was started for, and runs AFTER every provider call: the callback opens no transaction
/// (P5), so an implementation that writes owns its own statements.
/// </para>
/// </remarks>
internal interface ILinkCompletion
{
    /// <summary>
    /// Completes the link of a verified identity to the account the state cookie named and answers where the
    /// browser goes next.
    /// </summary>
    /// <param name="request">The verified identity, the account the link was started for, the allowlisted
    /// <c>returnUrl</c>, the staged invite token (if any) and the response.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// Success with the redirect target (an app path, <c>Location</c>-safe), or a failure rendered as the
    /// 00 §9.2 body. The story's own refusals are 409 when the identity belongs to another account that still
    /// holds a membership or a live session (never an auto-merge), and 503 <c>unavailable</c> with
    /// <c>Retry-After</c> for an infrastructure fault.
    /// </returns>
    ValueTask<Result<string>> CompleteAsync(LoginCompletionRequest request, CancellationToken cancellationToken);
}
