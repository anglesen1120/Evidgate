namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>
/// Who is acting (00 §5.5 C2 stage 4, 02 §5): the C2 authorization stage resolves
/// the caller's permission through a different path per kind, so the kind is part
/// of the contract rather than an inferred property of the request.
/// </summary>
public enum PrincipalKind
{
    /// <summary>A human member of the org: roles come from <c>membership_role</c> (02 §4.1).</summary>
    Member,

    /// <summary>
    /// A service identity — the CI producer of FR-02-7 or an agent of 02.5. Its
    /// permission set travels in the token claims and is bounded by the catalogue.
    /// </summary>
    ServicePrincipal,

    /// <summary>
    /// Evidgate staff. No standing tenant access: 02.8.1's <c>support_grant</c> scope
    /// is what may open a tenant, and until it ships this path fails closed.
    /// </summary>
    Operator,
}

/// <summary>
/// The authenticated caller of the current request, as the entry point resolved it
/// (02.6.2's principal-resolution step publishes it next to the request tenant,
/// exactly like <c>IRequestTenantContext</c>). The C2 authorization stage reads
/// this interface only: whether a member holds a permission is a tenant-row
/// question (the 02.2.1-T2 evaluator's SQL), whether a service identity holds one
/// is a claim-set question, and neither is answerable from the request body —
/// which is why the tenant id is also never taken from input (00 §5.5 C2).
/// </summary>
public interface IRequestPrincipal
{
    /// <summary>Which resolution path applies to this caller.</summary>
    PrincipalKind Kind { get; }

    /// <summary>The <c>user</c> the membership is bound to; set for <see cref="PrincipalKind.Member"/> only.</summary>
    Guid? UserId { get; }

    /// <summary>
    /// The single project a service token is scoped to (FR-02-7), or null for a
    /// member (membership is org-level in V1, 02.1.2 §4) and for an org-wide token.
    /// A request addressing another project is 403 <c>project_scope_mismatch</c>.
    /// </summary>
    Guid? ProjectScope { get; }

    /// <summary>
    /// The permission strings the token carries, for
    /// <see cref="PrincipalKind.ServicePrincipal"/> only. The set must be a subset of
    /// the catalogue: a claim the catalogue does not declare denies the request.
    /// </summary>
    IReadOnlyCollection<string> ClaimedPermissions { get; }

    /// <summary>
    /// The provider-verified email of a <see cref="PrincipalKind.Member"/>, when the
    /// entry point resolved one (02.3.1 login), else null. Never taken from request
    /// input; 02.1.1's org creation binds the creator's membership to it (T98).
    /// </summary>
    string? VerifiedEmail { get; }
}

/// <summary>
/// The immutable caller of one request. Built through the factories below, which
/// enforce each kind's invariants (a member has a user and no claim set, a service
/// identity has a claim set, an operator has neither) so no stage has to guess
/// which fields a given kind actually carries.
/// </summary>
public sealed class RequestPrincipal : IRequestPrincipal
{
    private static readonly IReadOnlyCollection<string> NoPermissions = [];

    private RequestPrincipal(
        PrincipalKind kind,
        Guid? userId,
        Guid? projectScope,
        IReadOnlyCollection<string> claimedPermissions,
        string? verifiedEmail = null)
    {
        Kind = kind;
        UserId = userId;
        ProjectScope = projectScope;
        ClaimedPermissions = claimedPermissions;
        VerifiedEmail = verifiedEmail;
    }

    /// <summary>A human member of the org the request runs as.</summary>
    public static RequestPrincipal Member(Guid userId, string? verifiedEmail = null) =>
        new(PrincipalKind.Member, userId, projectScope: null, NoPermissions, verifiedEmail);

    /// <summary>
    /// A service identity: the permission set its token carries, optionally scoped
    /// to exactly one project.
    /// </summary>
    public static RequestPrincipal Service(Guid? projectScope, IReadOnlyCollection<string> claimedPermissions)
    {
        ArgumentNullException.ThrowIfNull(claimedPermissions);

        return new RequestPrincipal(PrincipalKind.ServicePrincipal, userId: null, projectScope, claimedPermissions);
    }

    /// <summary>
    /// An Evidgate staff caller. 02.8.1 replaces this with the grant-carrying
    /// operator principal; until then the evaluator denies the kind outright.
    /// </summary>
    public static RequestPrincipal Operator() =>
        new(PrincipalKind.Operator, userId: null, projectScope: null, NoPermissions);

    /// <inheritdoc />
    public PrincipalKind Kind { get; }

    /// <inheritdoc />
    public Guid? UserId { get; }

    /// <inheritdoc />
    public Guid? ProjectScope { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ClaimedPermissions { get; }

    /// <inheritdoc />
    public string? VerifiedEmail { get; }
}
