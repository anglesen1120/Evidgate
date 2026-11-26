using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Deploy targets: the <c>If-Match</c> writes, soft delete, access rules and the table's own guards.</summary>
public sealed partial class DeployTargetTests
{
    [Fact]
    public async Task Patch_WithTheCurrentVersion_AppliesAndBumpsTheVersion_AndAudits()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "staging" })).Id();

        var patched = await PatchAsync(world, target, "\"1\"", new { name = "Staging-EU", order = 5 });

        patched.Status.ShouldBe(HttpStatusCode.OK, patched.ErrorMessage);
        patched.Text("name").ShouldBe("Staging-EU");
        patched.Body.GetProperty("order").GetInt32().ShouldBe(5);
        patched.Body.GetProperty("version").GetInt32().ShouldBe(2);
        patched.ETag.ShouldBe("\"2\"");
        (await AuditCountAsync(world, "deploy_target.update")).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_WithAStaleIfMatch_Is412_AndChangesNothing()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "staging" })).Id();
        (await PatchAsync(world, target, "\"1\"", new { order = 5 })).Status.ShouldBe(HttpStatusCode.OK);

        var stale = await PatchAsync(world, target, "\"1\"", new { name = "renamed" });

        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        Detail(stale, 1).ShouldBe(("version", "2"));
        (await _fixture.ScalarAsync<string>(world.Database, $"select name from public.deploy_target where id = '{target}'")).ShouldBe("staging");
        (await AuditCountAsync(world, "deploy_target.update")).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_WithoutIfMatch_Is422()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "staging" })).Id();

        var missing = await PatchAsync(world, target, null, new { order = 5 });
        var garbled = await PatchAsync(world, target, "abc", new { order = 5 });

        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        garbled.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _fixture.ScalarAsync<int>(world.Database, $"select version from public.deploy_target where id = '{target}'")).ShouldBe(1);
    }

    [Fact]
    public async Task TwoPatchesOfTheSameVersion_LeaveOneWinnerAndOne412()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "staging" })).Id();

        var answers = await Task.WhenAll(
            PatchAsync(world, target, "\"1\"", new { order = 1 }),
            PatchAsync(world, target, "\"1\"", new { order = 2 }));

        answers.Select(a => a.Status).Order().ToArray().ShouldBe([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed]);
        (await AuditCountAsync(world, "deploy_target.update")).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_ARenameOntoAnotherName_Is409TargetExists_IgnoringCase()
    {
        await using var world = await StartAsync();
        await CreateAsync(world, new { name = "prod" });
        var staging = (await CreateAsync(world, new { name = "staging" })).Id();

        var clash = await PatchAsync(world, staging, "\"1\"", new { name = "PROD" });

        clash.Status.ShouldBe(HttpStatusCode.Conflict);
        clash.ErrorCode.ShouldBe("target_exists");
        (await _fixture.ScalarAsync<string>(world.Database, $"select name from public.deploy_target where id = '{staging}'")).ShouldBe("staging");
    }

    [Fact]
    public async Task Patch_ANullPin_ClearsIt_AndAnEmptyOrUnknownBodyIs422()
    {
        await using var world = await StartAsync();
        var revision = await InsertRevisionAsync(world, world.Project);
        var target = (await CreateAsync(world, new { name = "prod", policy_revision_id = revision })).Id();

        (await PatchAsync(world, target, "\"1\"", new { })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PatchAsync(world, target, "\"1\"", new { color = "red" })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var cleared = await PatchAsync(world, target, "\"1\"", new { policy_revision_id = (Guid?)null });

        cleared.Status.ShouldBe(HttpStatusCode.OK, cleared.ErrorMessage);
        cleared.Body.GetProperty("policy_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Delete_DisablesTheTarget_KeepsTheRow_AndReservesTheName()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "prod" })).Id();

        var disabled = await DisableAsync(world, target, "\"1\"");

        disabled.Status.ShouldBe(HttpStatusCode.OK, disabled.ErrorMessage);
        disabled.Text("state").ShouldBe("disabled");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.deploy_target where id = '{target}'")).ShouldBe("disabled");
        (await AuditCountAsync(world, "deploy_target.disable")).ShouldBe(1);
        (await CreateAsync(world, new { name = "PROD" })).ErrorCode.ShouldBe("target_exists");
        (await DisableAsync(world, target, "\"2\"")).Status.ShouldBe(HttpStatusCode.Conflict);
        (await AuditCountAsync(world, "deploy_target.disable")).ShouldBe(1);

        var enabled = await PatchAsync(world, target, "\"2\"", new { state = "active" });
        enabled.Status.ShouldBe(HttpStatusCode.OK, enabled.ErrorMessage);
        enabled.Text("state").ShouldBe("active");
    }

    [Fact]
    public async Task Delete_WithAStaleIfMatch_Is412_AndTheTargetStaysActive()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "prod" })).Id();

        var stale = await DisableAsync(world, target, "\"7\"");

        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.deploy_target where id = '{target}'")).ShouldBe("active");
    }

    [Fact]
    public async Task TheTable_NeverDeletesARow_NotEvenForTheOwner()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "prod" })).Id();

        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() =>
            _fixture.ExecuteAsync(world.Database, $"delete from public.deploy_target where id = '{target}'"));

        error.Message.ShouldContain("deploy_target_immutable");
        (await CountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Writes_WithoutProjectWrite_Are403_AndAViewerStillReads()
    {
        await using var world = await StartAsync();
        var target = (await CreateAsync(world, new { name = "prod" })).Id();
        var viewer = TenancyApiFactory.Resolved(user: await AddMemberAsync(world, "viewer"), orgHeader: world.Org);

        (await CreateAsync(world, new { name = "x" }, caller: viewer)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await PatchAsync(world, target, "\"1\"", new { order = 3 }, caller: viewer)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await DisableAsync(world, target, "\"1\"", caller: viewer)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/deploy-targets/{target}", viewer)).Status.ShouldBe(HttpStatusCode.OK);
        (await CountAsync(world)).ShouldBe(1);
        (await AuditCountAsync(world, "deploy_target.update")).ShouldBe(0);
    }

    [Fact]
    public async Task ATargetOfAnotherProjectOrTenant_Is404()
    {
        await using var world = await StartAsync();
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        var target = (await CreateAsync(world, new { name = "prod" })).Id();
        var stranger = Guid.CreateVersion7();
        var strangerOrg = await CreateOrgAsync(world.Client, stranger, Unique.Slug("other"));
        var strangerCaller = TenancyApiFactory.Resolved(user: stranger, orgHeader: strangerOrg);

        // Through a sibling project of the same org.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{other}/deploy-targets/{target}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await PatchAsync(world, target, "\"1\"", new { order = 3 }, project: other)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await DisableAsync(world, target, "\"1\"", project: other)).Status.ShouldBe(HttpStatusCode.NotFound);

        // As another tenant's admin, on this org's project.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/deploy-targets/{target}", strangerCaller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/deploy-targets", strangerCaller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await CreateAsync(world, new { name = "x" }, caller: strangerCaller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await PatchAsync(world, target, "\"1\"", new { order = 3 }, caller: strangerCaller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await DisableAsync(world, target, "\"1\"", caller: strangerCaller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<int>(world.Database, $"select version from public.deploy_target where id = '{target}'")).ShouldBe(1);
    }

    private Task<Guid> AddMemberAsync(World world, string roleCode) => AddMemberAsync(world.Database, world.Org, roleCode);

    private async Task<Guid> AddMemberAsync(string database, Guid org, string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{org}', '{userId}', 'member-{userId:N}@example.test', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{org}', '{membershipId}', r.id
              from public.role r
             where r.code = '{roleCode}' and r.org_id is null;
            """);
        await AssertRoleSeededAsync(_fixture, database, org, userId, roleCode);

        return userId;
    }
}
