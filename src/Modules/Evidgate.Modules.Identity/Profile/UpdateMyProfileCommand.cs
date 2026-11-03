using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Resolve;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Sessions;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Identity.Profile;

/// <summary>
/// <c>PATCH /v1/me {preferred_locale}</c>: writes the one member of the own-profile contract the
/// account may change, then records the change in the audit chain of every active org it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pre-tenant, so a plain class.</b> The acting user comes from the presented session proof
/// alone: the write predicate and the liveness predicate run in one statement inside the definer,
/// so no caller can name another account, and nothing here needs a tenant transaction to exist.
/// </para>
/// <para>
/// <b>The order of the two effects.</b> The row is written and committed first, then the audit rows,
/// one chain per active org of the account (the resolver answers at most 200 orgs), through the same
/// writer the login and link paths use. That fan-out is best-effort and fail-closed: every fault —
/// a database error, a timeout, a busy chain, or the whole fan-out missing its
/// <see cref="AuditDeadline"/> — answers the one 503 this command has, with <c>Retry-After</c>. The
/// change is already stored when that 503 is sent, so it can arrive with some orgs audited and the
/// rest not, and a retry (with the version the caller's next read shows) can append a second row for
/// an org that was already written. No path returns success for a change whose record is incomplete.
/// </para>
/// </remarks>
/// <param name="store">The write statement.</param>
/// <param name="memberOrgs">The orgs whose chains receive the row.</param>
/// <param name="audit">The per-org audit writer.</param>
internal sealed class UpdateMyProfileCommand(
    IMyProfileStore store,
    IMemberOrgResolver memberOrgs,
    IIdentityAuditWriter audit)
{
    /// <summary>The metadata key the row carries — on the audit allowlist, never free text.</summary>
    internal const string LocaleMetadataKey = "locale";

    private const string ActiveOrgStatus = "active";

    /// <summary>
    /// How long the whole per-org fan-out may take. The profile row is already committed, so a chain
    /// that is slow or busy must end in the command's own 503 rather than hold the request open or
    /// surface as an outage the caller cannot act on.
    /// </summary>
    internal static readonly TimeSpan AuditDeadline = TimeSpan.FromSeconds(5);

    /// <summary>412: the version the caller held is no longer the row's, so nothing was written.</summary>
    internal static Error Stale { get; } = new(
        ApiErrorCode.PreconditionFailed,
        "the profile changed since you read it; re-read it and retry with its current ETag",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details: [new ErrorDetail("If-Match", "stale")]);

    /// <summary>503: the change is stored, but at least one org's audit chain could not be extended.</summary>
    internal static Error Unavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "the profile change could not be recorded; retry shortly",
        503,
        Retryable: true);

    /// <summary>Writes the locale against the version the caller read.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="preferredLocale">The validated language (<c>vi</c> / <c>en</c>).</param>
    /// <param name="expectedVersion">The <c>version</c> the caller's <c>If-Match</c> named.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The written profile; 401 <c>unauthenticated</c>; 412 when the version is stale; or 503 when the
    /// change could not be audited.
    /// </returns>
    public async ValueTask<Result<MyProfile>> ExecuteAsync(
        byte[] sessionProof,
        string preferredLocale,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var (status, profile, _) = await store
            .UpdateAsync(sessionProof, preferredLocale, expectedVersion, cancellationToken)
            .ConfigureAwait(false);

        switch (status)
        {
            case MyProfileStatus.Unauthenticated:
                return Result.Failure<MyProfile>(SessionErrors.Unauthenticated);
            case MyProfileStatus.Stale:
                return Result.Failure<MyProfile>(Stale);
            case MyProfileStatus.Ok:
                break;
            default:
                throw new InvalidOperationException($"the profile store answered the unknown status '{status}'");
        }

        return await AuditAsync(profile!, preferredLocale, cancellationToken).ConfigureAwait(false)
            ? Result.Success(profile!)
            : Result.Failure<MyProfile>(Unavailable);
    }

    /// <summary>
    /// One <c>user.locale_changed</c> row per active org of the acting account, each in its own tenant
    /// transaction through the identity audit writer, inside one deadline.
    /// </summary>
    /// <param name="profile">The row the write produced; its id is the actor and the object.</param>
    /// <param name="preferredLocale">The language the account chose.</param>
    /// <param name="cancellationToken">Request abort; it also rolls back the org being written.</param>
    /// <returns>
    /// True when every chain committed, false when any did not — a database fault, a busy chain or the
    /// deadline — so the route answers its one 503. A caller that left is not an answer at all.
    /// </returns>
    private async ValueTask<bool> AuditAsync(
        MyProfile profile,
        string preferredLocale,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AuditDeadline);

        try
        {
            var orgIds = (await memberOrgs.MemberOrgsAsync(profile.Id, deadline.Token).ConfigureAwait(false))
                .Where(org => string.Equals(org.Status, ActiveOrgStatus, StringComparison.Ordinal))
                .Select(org => org.OrgId)
                .ToArray();

            await audit
                .WriteAsync(
                    new IdentityAuditEvent(
                        IdentityAuditActions.LocaleChanged,
                        profile.Id,
                        Provider: null,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [LocaleMetadataKey] = preferredLocale,
                        }),
                    orgIds,
                    deadline.Token)
                .ConfigureAwait(false);

            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (AuditBusyException)
        {
            // A chain held its lock too long: the login path answers the same 503 for it, and this
            // command has one 503 shape rather than the middleware's.
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our deadline, not the caller leaving: the fan-out stops where it was and the change stays
            // stored with the remaining orgs unaudited.
            return false;
        }
    }
}
