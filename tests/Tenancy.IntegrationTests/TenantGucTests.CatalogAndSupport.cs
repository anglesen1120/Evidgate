namespace Tenancy.IntegrationTests;

public sealed partial class TenantGucTests
{
    private static async Task SeedDraftAsync(TenantScope scope, Guid orgId, Guid projectId, Guid authorMembershipId)
    {
        await scope.ExecuteAsync(
            """
            insert into public.draft
                (id, org_id, project_id, author_membership_id, kind, base_commit, content)
            values (@id, @org_id, @project_id, @author_membership_id, 'template', 'abcdef0', '{}'::jsonb)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("author_membership_id", authorMembershipId));
    }

    private static async Task SeedSupportGrantAsync(TenantScope scope, Guid orgId, Guid projectId, Guid operatorUserId)
    {
        await scope.ExecuteAsync(
            """
            insert into public.support_grant
                (id, org_id, operator_user_id, scopes, project_ids, reason, expires_at)
            values (@id, @org_id, @operator_user_id, array['case:read'], array[@project_id]::uuid[],
                    'Tenant visibility positive control', now() + interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("operator_user_id", operatorUserId));
    }
}
