namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The Dapper SQL of the 02.2.1-T2 access paths that are not the catalogue pages
/// of <see cref="IdentityReadSql"/>: the authorization evaluator's statements (one
/// permission, or the caller's whole set for the W7-SEC-02 self-escalation rule)
/// and the custom-role commands' catalogue validation. All of them run on the
/// ambient tenant scope (00 §5.5 C3/C5), so RLS supplies the row filter and named
/// parameters are the only way a value enters (00 §5.7 S1).
/// </summary>
internal static class IdentityAccessSql
{
    /// <summary>
    /// The member→grant join both access statements share (02.2.1-T2.2, AC7, P3): the
    /// membership subquery is declared by the caller, and the join walks
    /// <c>membership_role → role → role_permission → permission</c> for it. Written once
    /// so <see cref="MemberGrant"/> (one permission) and <see cref="MemberPermissionSet"/>
    /// (the caller's whole set, W7-SEC-02) can never drift apart: the self-escalation
    /// rule of the write commands and the verdict of the C2 gate are the same question
    /// about the same rows.
    /// <para>
    /// <c>mr.org_id = @org_id</c> is spelled out although the RLS policy on
    /// <c>membership_role</c> already implies it: it is the same composite key
    /// 02.1.2 §4 declares, so the join cannot depend on a policy being present for
    /// its correctness. <c>r.kind = 'member'</c> (W7-SEC-03) keeps a staff or service
    /// role out of a member's set even if one were attached to a membership row by
    /// hand — <c>ci_producer</c> carries <c>run:create</c>, <c>platform_operator</c>
    /// carries <c>support:read</c>, and neither belongs to a human. <c>rp.org_id is not
    /// distinct from r.org_id</c> (W7-SEC-05) pins a grant row to its role's own org, so
    /// an org-scoped row forged onto a built-in role (<c>r.org_id is null</c>) grants
    /// nothing. The join to <c>role</c> keeps the grants of an archived or foreign role
    /// out of a member's set the same way — under RLS a member sees the built-ins
    /// (<c>org_id is null</c>) and the org's own roles only.
    /// </para>
    /// <para>
    /// The <c>p.tenant_assignable</c> predicate is the read-time half of T79: a
    /// <c>role_permission</c> row corrupted to carry a code a member must never hold
    /// (billing, support, platform, agent-only) still grants nothing.
    /// </para>
    /// </summary>
    private const string MemberGrantJoin = """
             from active_member m
             join public.membership_role mr
                  on mr.membership_id = m.id
                 and mr.org_id = @org_id
             join public.role r
                  on r.id = mr.role_id
                 and r.kind = 'member'
             join public.role_permission rp
                  on rp.role_id = r.id
                 and rp.org_id is not distinct from r.org_id
             join public.permission p
                  on p.code = rp.permission_code
            where p.tenant_assignable
        """;

    /// <summary>
    /// The member path of <c>IPermissionEvaluator</c> (02.2.1-T2.2, AC7, P3): ONE
    /// statement that answers both "is the caller an active member of this org" and
    /// "does that membership's role set grant this permission". The membership
    /// subquery runs once (<c>with</c>) and the grant test is the shared
    /// <see cref="MemberGrantJoin"/> limited to one code.
    /// </summary>
    internal static readonly string MemberGrant = """
        with active_member as (
            select m.id
              from public.membership m
             where m.org_id = @org_id
               and m.user_id = @user_id
               and m.status = 'active'
        )
        select (select count(*) from active_member)::int as active_memberships,
               exists (
                   select 1
        """ + MemberGrantJoin + """
                     and rp.permission_code = @permission
               ) as granted
        """;

    /// <summary>
    /// The caller's whole member-assignable permission set in one statement (W7-SEC-02):
    /// the same <see cref="MemberGrantJoin"/> under the same active-membership
    /// subquery, projected as a set instead of a boolean. The write commands resolve the
    /// caller's effective set with this rather than by asking
    /// <see cref="MemberGrant"/> once per candidate code, so a create carrying 200 codes
    /// is still one round trip (P3's no-N+1 rule).
    /// </summary>
    internal static readonly string MemberPermissionSet = """
        with active_member as (
            select m.id
              from public.membership m
             where m.org_id = @org_id
               and m.user_id = @user_id
               and m.status = 'active'
        )
        select (select count(*) from active_member)::int as active_memberships,
               array(
                   select distinct rp.permission_code
        """ + MemberGrantJoin + """
               ) as codes
        """;

    /// <summary>
    /// The catalogue flags a custom-role command validates a submitted set against
    /// (02.2.1 §5): which codes exist at all, and which of them
    /// <c>tenant_assignable</c> forbids a member principal to hold (T79). One
    /// bounded statement over the platform catalogue — 55 rows of reference data
    /// (00 §2), so reading the catalogue rather than binding the submitted array is
    /// both cheaper and simpler: the Dapper path's registered <c>string[]</c> type
    /// handler is read-side only (writes are EF Core, C4/C5), so an array parameter
    /// has no writer to bind it.
    /// </summary>
    internal const string CatalogueFlags = """
        select code,
               tenant_assignable
          from public.permission
        """;
}

/// <summary>One row of <see cref="IdentityAccessSql.MemberGrant"/>.</summary>
/// <param name="ActiveMemberships">How many active memberships the caller has in the org (0 = 403 <c>no_membership</c>).</param>
/// <param name="Granted">True when the role set grants the requested, member-assignable permission.</param>
internal sealed record MemberGrantRow(int ActiveMemberships, bool Granted);

/// <summary>One row of <see cref="IdentityAccessSql.MemberPermissionSet"/> (W7-SEC-02).</summary>
/// <param name="ActiveMemberships">How many active memberships the caller has in the org (0 = the caller holds nothing).</param>
/// <param name="Codes">Every member-assignable code the caller's `member`-kind roles grant in the org (T79-filtered, W7-SEC-03/W7-SEC-05-joined).</param>
internal sealed record MemberPermissionSetRow(int ActiveMemberships, string[] Codes);

/// <summary>One row of <see cref="IdentityAccessSql.CatalogueFlags"/>.</summary>
/// <param name="Code">The submitted <c>resource:action</c> string.</param>
/// <param name="TenantAssignable">False when a member principal must never hold the code (T79).</param>
internal sealed record CatalogueFlagRow(string Code, bool TenantAssignable);
