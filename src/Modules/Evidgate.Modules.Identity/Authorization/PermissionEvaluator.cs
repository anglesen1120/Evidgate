using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Identity.Infrastructure.Persistence;

namespace Evidgate.Modules.Identity.Authorization;

/// <summary>
/// The 02.2.1-T2.2 permission evaluator: the Identity module's implementation of the
/// C2 pipeline's <see cref="IPermissionEvaluator"/> seam, registered by
/// <c>AddIdentityModule</c> in both entry points (so Api and Worker evaluate the
/// same way).
/// <para>
/// Three paths, one per <see cref="PrincipalKind"/>:
/// </para>
/// <list type="bullet">
///   <item>a <b>member</b> is resolved in the database — one statement under RLS
///   (P3) that answers membership and grant together (02 §5), with the result
///   filtered to <c>tenant_assignable</c> codes (TL T79);</item>
///   <item>a <b>service identity</b> carries its permission set in the token, and
///   that set must be a subset of the catalogue: a claim the catalogue does not
///   declare denies the request instead of granting it (02 §5, FR-02-7);</item>
///   <item>an <b>operator</b> is denied outright until 02.8.1 ships the
///   <c>support_grant</c> scope check — the fail-closed stub, deliberately not a
///   "no operator support yet, allow" path (02 §5, 00 §5.5 C2 stage 4).</item>
/// </list>
/// <para>
/// An unknown permission string denies on every path without a round trip: no
/// catalogue row can carry it, so no role set and no token claim could grant it
/// (02 §5 "unknown permission → deny", FR-02-2).
/// </para>
/// <para>
/// The class also implements <see cref="ICallerPermissionSet"/>: the write commands of
/// this module ask the same machinery for the caller's whole effective set before they
/// hand out a role or a custom-role grant (W7-SEC-02 — a <c>tenant_admin</c> may not
/// grant itself what 00 §2 says it never holds). One class, one join, two shapes of the
/// same question.
/// </para>
/// </summary>
internal sealed class PermissionEvaluator(ITenantDb db) : IPermissionEvaluator, ICallerPermissionSet
{
    /// <summary>The empty set a fail-closed resolution answers (never null).</summary>
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(context);

        return principal.Kind switch
        {
            PrincipalKind.Member => await MemberAsync(permission, principal, context, cancellationToken)
                .ConfigureAwait(false),
            PrincipalKind.ServicePrincipal => Service(permission, principal, context),
            PrincipalKind.Operator => OperatorWithoutSupportGrant(),
            _ => PermissionDecision.Denied(ApiErrorCode.Forbidden),
        };
    }

    /// <summary>
    /// The member path: one statement, then the verdict. Zero active memberships is
    /// the 403 <c>no_membership</c> case 02.1.2 AC2 and 02.3.1 AC3 document (no
    /// membership and no bound invite); an active member whose role set lacks the
    /// permission is plain <c>forbidden</c>.
    /// </summary>
    /// <remarks>
    /// The project dimension of FR-02-2 is org-level for a member in V1: 02.1.2 §4's
    /// <c>membership</c> and <c>membership_role</c> carry no project scope, so a
    /// member's roles apply to every project of the org, and a project-scoped member
    /// role would be a schema change (recorded in the 02.2.1 feature plan §3).
    /// Project-scoped evaluation therefore binds the service-identity path, whose
    /// token carries the single project claim of FR-02-7.
    /// </remarks>
    private async ValueTask<PermissionDecision> MemberAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken)
    {
        if (!Permissions.IsKnown(permission) || principal.UserId is not { } userId)
        {
            return PermissionDecision.Denied(ApiErrorCode.Forbidden);
        }

        var grant = await db.QuerySingleOrDefaultAsync<MemberGrantRow>(
            IdentityAccessSql.MemberGrant,
            new { org_id = context.OrgId, user_id = userId, permission },
            cancellationToken).ConfigureAwait(false);

        if (grant is null)
        {
            // The statement always answers exactly one row; a null means the read
            // did not reach the database — fail closed rather than guess a grant.
            return PermissionDecision.Denied(ApiErrorCode.Forbidden);
        }

        if (grant.ActiveMemberships == 0)
        {
            return PermissionDecision.Denied(ApiErrorCode.NoMembership);
        }

        return grant.Granted
            ? PermissionDecision.Granted
            : PermissionDecision.Denied(ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// The service-identity path (FR-02-7, AC4): the token's project claim must match
    /// the project the request runs as, and the token's permission set must be a
    /// subset of the catalogue before any of it counts — a token carrying a claim the
    /// catalogue does not declare is malformed, so it grants nothing rather than
    /// everything else it claims.
    /// </summary>
    private static PermissionDecision Service(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context)
    {
        if (principal.ProjectScope is { } scope && context.ProjectId != scope)
        {
            return PermissionDecision.Denied(ApiErrorCode.ProjectScopeMismatch);
        }

        // W7-SEC-06 / 02.6.2 AC6: a service token is bound to the service roles
        // (ci_producer, agent), so a claim outside their permissions makes the token
        // malformed — it holds nothing, not the rest of its claims.
        var claimsAreCatalogueCodes = principal.ClaimedPermissions.All(IsServiceClaim);

        return claimsAreCatalogueCodes
            && Permissions.IsKnown(permission)
            && principal.ClaimedPermissions.Contains(permission)
                ? PermissionDecision.Granted
                : PermissionDecision.Denied(ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// The operator stub (02.8.1): an Evidgate staff caller has no standing tenant
    /// access, and the story that grants it through a tenant-approved, expiring
    /// <c>support_grant</c> scope is not built yet. Until it is, the path denies —
    /// named, so the deferral is visible in the code rather than an implicit
    /// fall-through (02.2.1 §5, and the feature plan's recorded deferral).
    /// </summary>
    /// <summary>
    /// The permissions of the <c>kind: service</c> roles (<c>ci_producer</c>, <c>agent</c>)
    /// in <c>contracts/permissions.json</c>: the most a service token may ever claim.
    /// <c>ServiceRoleCeiling_MatchesTheCatalogue</c> pins this set to the file.
    /// </summary>
    internal static readonly IReadOnlySet<string> ServiceRoleCeiling = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.Artifact.Write,
        Permissions.Event.Write,
        Permissions.Run.Create,
        Permissions.Job.Claim,
        Permissions.Job.Execute,
    };

    private static bool IsServiceClaim(string code) =>
        Permissions.IsKnown(code) && ServiceRoleCeiling.Contains(code);

    private static PermissionDecision OperatorWithoutSupportGrant() =>
        PermissionDecision.Denied(ApiErrorCode.Forbidden);

    /// <summary>
    /// The member path's set-shaped answer (W7-SEC-02): one statement over the same
    /// join the per-permission verdict uses, projected as the caller's whole
    /// member-assignable code set instead of a single boolean. The write commands
    /// compare a grant against it before writing, so "may I hand this out?" is answered
    /// by the same rows that will answer "do I hold it?" on the next request.
    /// </summary>
    /// <remarks>
    /// Fail-closed in every branch that is not a resolved member: a caller with no
    /// active membership, a kind the module does not resolve, a null principal or a read
    /// that returned nothing all answer the empty set, and the caller's own commands
    /// then refuse anything that grants a permission. A service identity's set is its
    /// token claims bounded by the catalogue — the same bound
    /// <see cref="Service"/> applies — and an operator holds nothing until 02.8.1.
    /// </remarks>
    public async ValueTask<IReadOnlySet<string>> ResolveAsync(
        Guid orgId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        switch (principal.Kind)
        {
            case PrincipalKind.Member when principal.UserId is { } userId:
                var row = await db.QuerySingleOrDefaultAsync<MemberPermissionSetRow>(
                    IdentityAccessSql.MemberPermissionSet,
                    new { org_id = orgId, user_id = userId },
                    cancellationToken).ConfigureAwait(false);

                return row is null || row.ActiveMemberships == 0
                    ? NoPermissions
                    : row.Codes.ToHashSet(StringComparer.Ordinal);

            case PrincipalKind.ServicePrincipal:
                // Same bound as Service(): one claim outside the catalogue makes the
                // token malformed, so it holds nothing rather than the rest of its claims.
                return principal.ClaimedPermissions.All(IsServiceClaim)
                    ? principal.ClaimedPermissions.ToHashSet(StringComparer.Ordinal)
                    : NoPermissions;

            default:
                return NoPermissions;
        }
    }
}
