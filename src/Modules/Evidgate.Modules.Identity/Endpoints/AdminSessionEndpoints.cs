using System.Diagnostics;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Evidgate.Modules.Identity.Endpoints;

internal static partial class SessionEndpoints
{
    /// <summary>Maps the administrative bulk revoke onto the same <c>/v1/auth</c> group.</summary>
    /// <param name="group">The <c>/v1/auth</c> group <see cref="Map"/> composed.</param>
    internal static void MapAdminRevoke(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/users/{u}/sessions/revoke-all", RevokeAllForUserAdminAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("End every live session of one member of an org the caller administers");
    }

    /// <summary>
    /// <c>POST /v1/auth/users/{u}/sessions/revoke-all</c>: an administrator ends every live session of one member
    /// of an org they administer, in one call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is the single admin revoke's, one row wider: the caller is proved by the session cookie alone,
    /// the org is named by <c>X-Evidgate-Org</c>, and the <c>membership:write</c> check plus the target's ACTIVE
    /// membership in that SAME org run inside the definer, never here. Every refusal — the target is the caller,
    /// a missing or non-uuid org header, a caller who does not administer the org, an unknown or non-member
    /// target, an id that is not a uuid — answers the identical 404 <c>not_found</c> the single revoke answers,
    /// after authentication, so this route distinguishes nothing about other accounts.
    /// </para>
    /// <para>
    /// A call that ends at least one session writes one <c>auth.session_revoke_admin</c> row in the definer's own
    /// transaction, so a fault inside that transaction — a refused audit insert, a lock timeout, a deadlock —
    /// rolls the whole call back: nothing was ended and nothing was recorded, and a retry is safe. A failure
    /// after the commit is a different case: a lost response leaves the outcome unknown, and the retry is safe
    /// there only because the route is idempotent — a call that finds no live session answers 204 and writes no
    /// second row. Zero live sessions is a 204 and records nothing. The call's request id and trace id ride
    /// along from the same two sources the C# audit writer reads.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="u">The target user id, bound as text and parsed only after the caller was authenticated.</param>
    /// <param name="authenticator">The deployment's session authenticator.</param>
    /// <param name="store">The session route statements.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>204 when the target has no live session left, otherwise the shared refusal.</returns>
    private static async Task<Result> RevokeAllForUserAdminAsync(
        HttpContext context,
        string u,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        // Authentication outranks the segment's shape, and the org header is REQUIRED here: without a well-formed
        // one there is no org whose administrator the caller could be, so the answer is the one 404.
        if (!Guid.TryParse(u, out var targetUserId) || OrgOf(context) is not { } orgId)
        {
            return Result.Failure(SessionNotFound);
        }

        SessionRouteStatus status;
        try
        {
            var activity = Activity.Current;

            (status, _) = await store
                .RevokeAllForUserAsAdminAsync(
                    proof,
                    targetUserId,
                    orgId,
                    activity?.GetBaggageItem(AuditTrail.RequestIdBaggage),
                    activity?.TraceId.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NpgsqlException) when (!cancellationToken.IsCancellationRequested)
        {
            // Either the definer aborted (lock timeout, deadlock, the audit row's refusal) and rolled the whole
            // call back, or the connection died after the commit and the outcome is unknown. A retry is safe in
            // both cases only because the route is idempotent: a call that finds no live session answers 204.
            return Result.Failure(SessionErrors.Unavailable);
        }

        return status switch
        {
            // Zero revoked is the same answer: the member is in the desired state and no session of theirs lives.
            SessionRouteStatus.Done => Result.Success(StatusCodes.Status204NoContent),
            SessionRouteStatus.NotFound => Result.Failure(SessionNotFound),
            SessionRouteStatus.Unauthenticated => Result.Failure(SessionErrors.Unauthenticated),
            _ => Result.Failure(SessionErrors.Unavailable),
        };
    }
}
