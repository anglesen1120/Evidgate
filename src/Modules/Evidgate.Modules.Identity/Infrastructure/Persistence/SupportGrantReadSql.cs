namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Read SQL of the tenant's support-grant list (02.8.1 §5): one statement per page, keyset on
/// <c>(created_at, id)</c> descending, served by <c>support_grant_org_created_idx</c>. The state is derived in the
/// statement from the stored instants, so a grant that expires between two pages changes state with no write.
/// </summary>
internal static class SupportGrantReadSql
{
    /// <summary>
    /// One page. Revocation wins over expiry, expiry over approval: a revoked row is <c>revoked</c> even before its
    /// deadline, and a grant whose window closed is <c>expired</c> whether or not it was approved — that is what the
    /// operator's own validity predicate reads. <c>@limit</c> is page size + 1.
    /// </summary>
    internal const string Page =
        """
        select id as Id, org_id as OrgId, operator_user_id as OperatorUserId, scopes as Scopes,
               project_ids as ProjectIds, reason as Reason,
               case
                 when revoked_at is not null then 'revoked'
                 when expires_at <= now() then 'expired'
                 when approved_at is not null then 'approved'
                 else 'requested'
               end as State,
               approved_by as ApprovedBy, approved_at as ApprovedAt,
               expires_at as ExpiresAt, revoked_at as RevokedAt, created_at as CreatedAt
          from public.support_grant
         where org_id = @org_id
           and (@cursor_created_at::timestamptz is null or (created_at, id) < (@cursor_created_at, @cursor_id::uuid))
         order by created_at desc, id desc
         limit @limit
        """;
}

/// <summary>Dapper shape of <see cref="SupportGrantReadSql.Page"/>.</summary>
internal sealed class SupportGrantPageRow
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid OperatorUserId { get; set; }

    public string[] Scopes { get; set; } = [];

    public Guid[] ProjectIds { get; set; } = [];

    public string Reason { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
