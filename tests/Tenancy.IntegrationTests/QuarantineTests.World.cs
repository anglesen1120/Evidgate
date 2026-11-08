using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The world of <see cref="QuarantineTests"/> and the arrange/act helpers its cases share:
/// one org, one project, three catalog cases and an active <c>test_maintainer</c> seeded
/// through the owner seam. The cases themselves are in <c>QuarantineTests.cs</c>.
/// </summary>
public sealed partial class QuarantineTests
{
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        IReadOnlyList<Guid> Cases,
        Guid Maintainer,
        Guid MaintainerMembership) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public Action<HttpRequestHeaders> AsMaintainer => TenancyApiFactory.Resolved(user: Maintainer, orgHeader: Org);

        public Guid Case => Cases[0];

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(string? database = null)
    {
        // The plan test passes a dedicated database: it disables sequential scans and reads the planner's choice,
        // which a table other classes seed at the same time would change.
        database ??= await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));
        var (cases, maintainer, membership) = await SeedCasesAndMaintainerAsync(database, org, project);

        return new World(api, client, database, user, org, project, cases, maintainer, membership);
    }

    // ------------------------------------------------------------------ helpers

    private static Action<HttpRequestHeaders> As(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    /// <summary>Three catalog cases and an active <c>test_maintainer</c> membership (the only role holding <c>quarantine:write</c>, D1).</summary>
    private async Task<(IReadOnlyList<Guid> Cases, Guid User, Guid Membership)> SeedCasesAndMaintainerAsync(
        string database, Guid org, Guid project)
    {
        var cases = new List<Guid>();
        var maintainer = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // 0044's `membership_user_fk` makes the membership's user_id name a real account.
            await scope.ExecuteAsync(
                """insert into public."user" (id, email) values (@id, @email)""",
                ("id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            await scope.ExecuteAsync(
                "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
                ("id", membership), ("org_id", org), ("user_id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            // The role row the membership route writes, seeded as the owner: the route refuses a role whose own
            // permissions the caller does not hold, so the fixture cannot hand this one out through the API.
            (await scope.ExecuteAsync(
                """
                insert into public.membership_role (id, org_id, membership_id, role_id)
                select gen_random_uuid(), @org_id, @membership_id, r.id
                  from public.role r
                 where r.code = 'test_maintainer' and r.org_id is null
                """,
                ("org_id", org), ("membership_id", membership))).ShouldBe(
                1, "the fixture seeded the role 'test_maintainer' once: an unknown role code seeds nothing");
            for (var i = 0; i < 3; i++)
            {
                var caseId = Guid.CreateVersion7();
                cases.Add(caseId);
                await scope.ExecuteAsync(
                    """
                    insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
                    values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
                    """,
                    ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()), ("name", $"case {i}"));
            }

            await scope.CommitAsync();
        }

        return (cases, maintainer, membership);
    }

    private static object Body(
        World world,
        string reason = "known failing case",
        DateTimeOffset? expiresAt = null,
        Guid? project = null,
        Guid? caseId = null,
        Guid? owner = null) => new
        {
            project_id = project ?? world.Project,
            case_id = caseId ?? world.Case,
            owner = owner ?? world.MaintainerMembership,
            reason,
            expires_at = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
        };

    private static Task<ApiAnswer> CreateAsync(World world, object body, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/quarantine", caller ?? world.AsMaintainer, body, key ?? NewKey());

    private static Task<ApiAnswer> RemoveAsync(World world, Guid entry, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Delete, $"/v1/quarantine/{entry}", caller ?? world.AsMaintainer, null, key ?? NewKey());

    private static Task<ApiAnswer> ListAsync(World world, Guid project, string query = "", Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/quarantine{query}", caller ?? world.AsMaintainer);

    /// <summary>An active member of the org holding the built-in role, seeded as the owner (the route refuses a role the caller cannot hand out).</summary>
    private Task<Guid> AddMemberAsync(World world, string roleCode) => AddMemberAsync(world, world.Org, roleCode);

    /// <summary>An active member of <paramref name="org"/> holding the built-in role, seeded as the owner.</summary>
    private async Task<Guid> AddMemberAsync(World world, Guid org, string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{org}', '{userId}', 'member-{userId:N}@example.test', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{org}', '{membershipId}', r.id
              from public.role r
             where r.code = '{roleCode}' and r.org_id is null;
            """);
        await AssertRoleSeededAsync(_fixture, world.Database, org, userId, roleCode);

        return userId;
    }

    /// <summary>Inserts a <c>policy_revision</c> in a chosen state as the owner: activation merged with 07.1.1-T4 and the API refuses to activate a revision whose critical case is under an active quarantine (Q2), so the state is seeded directly.</summary>
    private async Task<Guid> InsertRevisionAsync(World world, string state, IReadOnlyList<string> criticalCases, int revisionNo = 1)
    {
        var id = Guid.CreateVersion7();
        var body = JsonSerializer.Serialize(new { critical_cases = criticalCases });
        var hash = "sha256:" + new string((char)('a' + revisionNo), 64);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by)
            values ('{id}', '{world.Org}', '{world.Project}', {revisionNo}, '{hash}', '{body}'::jsonb, '{state}', '{world.User}')
            """)).ShouldBe(1);
        await owner.CommitAsync();

        return id;
    }

    /// <summary>Inserts an <c>active</c> entry whose <c>expires_at</c> is already past, as the owner (the sweeper has not run).</summary>
    private async Task<Guid> InsertExpiredActiveAsync(World world, Guid caseId)
    {
        var id = Guid.CreateVersion7();

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at)
            values ('{id}', '{world.Org}', '{world.Project}', '{caseId}', '{world.MaintainerMembership}', 'expired', 'active', now() - interval '1 hour')
            """)).ShouldBe(1);
        await owner.CommitAsync();

        return id;
    }

    private async Task<long> AuditCountAsync(World world, string action) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where action = '{action}' and org_id = '{world.Org:D}'");

    private static (string Path, string Reason) Detail(ApiAnswer answer, int index = 0)
    {
        var detail = answer.Body.GetProperty("error").GetProperty("details")[index];
        return (detail.GetProperty("path").GetString()!, detail.GetProperty("reason").GetString()!);
    }
}
