using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;

namespace Evidgate.Modules.Identity.Profile;

/// <summary>
/// <c>GET /v1/me</c>: the signed-in account's own profile, resolved from the session the
/// request presented.
/// </summary>
/// <remarks>
/// A plain class rather than a pipeline message: the route runs before any tenant exists, and
/// the request-path stages that make a message a business message (tenant transaction,
/// permission, idempotency claim) all need one. The session routes are pre-tenant the same way.
/// </remarks>
/// <param name="store">The two definer statements the route is allowed to make.</param>
internal sealed class GetMyProfileQuery(IMyProfileStore store)
{
    /// <summary>Reads the caller's own row.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The profile, or 401 <c>unauthenticated</c> when the proof resolves no live session.</returns>
    public async ValueTask<Result<MyProfile>> ExecuteAsync(
        byte[] sessionProof,
        CancellationToken cancellationToken)
    {
        var (status, profile) = await store.ReadAsync(sessionProof, cancellationToken).ConfigureAwait(false);

        return status == MyProfileStatus.Ok
            ? Result.Success(profile!)
            : Result.Failure<MyProfile>(SessionErrors.Unauthenticated);
    }
}
