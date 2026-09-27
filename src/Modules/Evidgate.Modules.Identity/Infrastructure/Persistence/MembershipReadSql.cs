namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// One <c>membership</c> row as the Dapper read path projects it (02.1.2-T1.2, 00
/// §5.5 C5). The <b>column order of every statement below is load-bearing</b>:
/// Dapper builds a positional-record materialiser from the constructor, so a column
/// read in a different order leaves a field at its default (M1W3-02) — which is what
/// <c>MembershipReadSql</c>'s statements and this record must agree on.
/// </summary>
/// <param name="Id">The membership id.</param>
/// <param name="OrgId">The owning org.</param>
/// <param name="UserId">The bound user, or null while the invite is pending.</param>
/// <param name="InvitedEmail">The normalised invite address.</param>
/// <param name="Status">One of <c>MembershipStatus</c>.</param>
/// <param name="CreatedAt">Creation instant — the keyset's leading column (00 §9.1).</param>
/// <param name="UpdatedAt">Last write instant.</param>
/// <param name="Version">Optimistic-concurrency token.</param>
internal sealed record MembershipRow(
    Guid Id,
    Guid OrgId,
    Guid? UserId,
    string InvitedEmail,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>
/// One <c>membership_role</c> row joined to its <c>role</c>, prefixed with the
/// membership it belongs to so one bounded statement can serve a whole page.
/// </summary>
/// <param name="MembershipId">The membership the role is granted to.</param>
/// <param name="Id">The role id.</param>
/// <param name="OrgId">Null for a built-in role, the owning org for a custom one.</param>
/// <param name="Code">Role code (00 §2).</param>
/// <param name="Name">Display name.</param>
/// <param name="IsBuiltin">True for a catalogue role.</param>
/// <param name="CreatedAt">Role creation instant — the roles' stable order.</param>
/// <param name="Version">Optimistic-concurrency token.</param>
internal sealed record MembershipRoleRow(
    Guid MembershipId,
    Guid Id,
    Guid? OrgId,
    string Code,
    string Name,
    bool IsBuiltin,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>
/// The Dapper read SQL of memberships (02.1.2-T1.2, 00 §5.5 C5). Every statement is
/// bounded, uses named parameters only (00 §5.7 S1), and runs inside
/// <c>ITenantScope.BeginAsync</c> with <c>SET TRANSACTION READ ONLY</c> (C1/T95), so
/// RLS supplies the tenant filter: a membership of another org is indistinguishable
/// from a missing one (AC7).
/// </summary>
/// <remarks>
/// The write side of the same tables is the module's EF Core model, exactly as
/// 00 §5.5 C4/C5 split it.
/// </remarks>
internal static class MembershipReadSql
{
    /// <summary>One membership by id (<c>GET /v1/orgs/{o}/memberships/{m}</c>).</summary>
    internal const string MembershipById = """
        select id,
               org_id,
               user_id,
               invited_email,
               status,
               created_at,
               updated_at,
               version
          from public.membership
         where org_id = @org_id
           and id = @membership_id
        """;

    /// <summary>
    /// One page of an org's memberships, keyset-ordered on <c>(created_at, id)</c> per
    /// 00 §9.1 and served by <c>membership_org_created_id_idx</c> (0011). The org
    /// predicate is explicit: RLS would filter too, but an equality on the index's
    /// leading column is what lets the planner walk the index in key order.
    /// </summary>
    internal const string MembershipsPage = """
        select id,
               org_id,
               user_id,
               invited_email,
               status,
               created_at,
               updated_at,
               version
          from public.membership
         where org_id = @org_id
           and (@cursor_created_at::timestamptz is null
                or (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid))
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>
    /// The roles of the page's memberships in one statement (no N+1, 02.1.2 §8 P3).
    /// The join is on <c>role.id</c> alone: built-in roles carry <c>org_id IS NULL</c>,
    /// so an org column could not take part (see <c>MembershipRole</c>); RLS on
    /// <c>role</c> and on <c>membership_role</c> is what bounds the result.
    /// </summary>
    internal const string RolesOfMemberships = """
        select membership_role.membership_id,
               role.id,
               role.org_id,
               role.code,
               role.name,
               role.is_builtin,
               role.created_at,
               role.version
          from public.membership_role
          join public.role on role.id = membership_role.role_id
         where membership_role.org_id = @org_id
           and membership_role.membership_id = any(@membership_ids::uuid[])
         order by membership_role.membership_id,
                  role.created_at,
                  role.id
        """;

    /// <summary>
    /// The org's active memberships holding <c>role_code</c>, locked for
    /// the duration of the command's transaction (02.1.2 §8 "Last-admin guard":
    /// <c>SELECT … FOR UPDATE</c> on the org's admin rows). Two concurrent commands
    /// that would each drop an admin therefore serialise: the second one re-evaluates
    /// the rows the first one changed and sees the smaller set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>`membership_role` is locked too, and that is load-bearing.</b> The guard's
    /// answer depends on the join to <c>membership_role</c>, and a demotion
    /// (<c>RevokeRoleCommand</c>) changes only that row — the <c>membership</c> row it
    /// precedes stays <c>active</c>. PostgreSQL's READ COMMITTED re-check after waiting
    /// for a row lock (EvalPlanQual) re-reads only the locked row; it deliberately does
    /// not see a concurrent command's effect on *other* rows. With
    /// <c>for update of membership</c> alone the loser of a demotion race unblocked,
    /// re-read its own unchanged <c>membership</c> row, still counted the role grant the
    /// winner had already deleted, and demoted too — leaving the org with no admin at
    /// all (measured: two concurrent demotions of the only two admins both answered 204
    /// and the org kept zero active <c>tenant_admin</c> members). Locking the
    /// <c>membership_role</c> row makes the deleted grant itself the contended row, so
    /// the loser waits on it and, once the winner commits, drops the vanished row from
    /// its result set (409 <c>last_admin</c>).
    /// </para>
    /// <para>
    /// Both callers run this same statement (they serialise before the write), so the
    /// lock order is identical and cannot deadlock; <c>role</c> is not locked — it is
    /// never mutated by these commands.
    /// </para>
    /// </remarks>
    internal const string ActiveMembershipsHoldingRole = """
        select membership.id
          from public.membership
          join public.membership_role
            on membership_role.org_id = membership.org_id
           and membership_role.membership_id = membership.id
          join public.role on role.id = membership_role.role_id
         where membership.org_id = @org_id
           and membership.status = 'active'
           and role.code = @role_code
         order by membership.id
           for update of membership, membership_role
        """;

    /// <summary>The membership_role row of one (membership, role) pair, if granted.</summary>
    internal const string MembershipRolePair = """
        select id
          from public.membership_role
         where org_id = @org_id
           and membership_id = @membership_id
           and role_id = @role_id
        """;

    /// <summary>The active member roles of one user in one org (<c>IIdentityDirectory</c>).</summary>
    internal const string ActiveMemberRoles = """
        select role.id,
               role.org_id,
               role.code,
               role.name,
               role.is_builtin,
               role.created_at,
               role.version
          from public.membership
          join public.membership_role
            on membership_role.org_id = membership.org_id
           and membership_role.membership_id = membership.id
          join public.role on role.id = membership_role.role_id
         where membership.org_id = @org_id
           and membership.user_id = @user_id
           and membership.status = 'active'
         order by role.created_at,
                  role.id
        """;

    /// <summary>The pending invites of one org addressed to one normalised address.</summary>
    internal const string PendingInvitesForEmail = """
        select id,
               org_id,
               invited_email,
               status,
               invite_expires_at
          from public.membership
         where org_id = @org_id
           and invited_email = @invited_email
           and status = 'pending'
         order by created_at,
                  id
        """;

    /// <summary>The one membership of a user in an org (<c>unique (org_id, user_id) where user_id is not null</c>).</summary>
    internal const string MembershipForUser = """
        select id,
               org_id,
               user_id,
               status
          from public.membership
         where org_id = @org_id
           and user_id = @user_id
        """;

    /// <summary>The role ids of one membership.</summary>
    internal const string RoleIdsOfMembership = """
        select role_id
          from public.membership_role
         where org_id = @org_id
           and membership_id = @membership_id
         order by role_id
        """;
}
