using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Who may reach the draft routes at all: an anonymous caller, a member without the write permission and a member
/// without the read permission are refused before a body or an <c>If-Match</c> is looked at, so a malformed request
/// cannot tell an unauthorised caller what the parser thinks of it.
/// </summary>
public sealed partial class DraftTests
{
    [Fact]
    public async Task Unauthenticated_Is401BeforeValidation_OnAllFourRoutes()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);
        Action<HttpRequestHeaders> anonymous = _ => { };

        var answers = new[]
        {
            // An unreadable body, an out-of-range limit and a valid route all answer 401: authentication comes
            // before the body cap, the query rules and the handler.
            await world.Client.SendRawAsync(HttpMethod.Post, world.Collection, anonymous, "{ not json"),
            await world.Client.SendRawAsync(HttpMethod.Patch, World.Draft(id), anonymous, "{ not json"),
            await world.Client.SendAsync(HttpMethod.Get, $"{world.Collection}?limit=9999", anonymous),
            await world.Client.SendAsync(HttpMethod.Get, World.Draft(id), anonymous),
        };

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.Unauthorized, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("unauthenticated");
        }

        (await StoredAsync<int>(world, id, "version")).ShouldBe(1, "no anonymous request reached a mutation");
    }

    [Fact]
    public async Task Viewer_CannotCreateOrPatch_ButReads()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);
        var viewer = await MemberAsync(world, roleCode: "viewer");

        // The refusals come before the body and the If-Match are read: both bodies are well-formed JSON that the
        // contract refuses anyway, so a 422/400 would mean the parser ran for a caller who may not write.
        var create = await world.Client.SendRawAsync(HttpMethod.Post, world.Collection, viewer, "{}");
        create.Status.ShouldBe(HttpStatusCode.Forbidden, create.ErrorMessage);
        create.ErrorCode.ShouldBe("forbidden");

        var patch = await world.Client.SendRawAsync(HttpMethod.Patch, World.Draft(id), viewer, "{}");
        patch.Status.ShouldBe(HttpStatusCode.Forbidden, patch.ErrorMessage);
        patch.ErrorCode.ShouldBe("forbidden");

        // The viewer holds case:read, so the two reads of the same draft are the positive control of the pair.
        (await world.Client.SendAsync(HttpMethod.Get, world.Collection, viewer))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(world, id, viewer)).Status.ShouldBe(HttpStatusCode.OK);
        (await StoredAsync<int>(world, id, "version")).ShouldBe(1, "a refused writer changed nothing");
    }

    [Fact]
    public async Task MemberWithoutCaseRead_CannotListOrGetOrWrite()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);
        var noRole = await MemberAsync(world, roleCode: null);

        foreach (var answer in new[]
                 {
                     await world.Client.SendAsync(HttpMethod.Get, world.Collection, noRole),
                     await world.Client.SendAsync(HttpMethod.Get, World.Draft(id), noRole),
                     await world.Client.SendAsync(HttpMethod.Post, world.Collection, noRole, Body(), NewKey()),
                     await PatchAsync(world, id, caller: noRole),
                 })
        {
            answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("forbidden");
        }

        (await StoredAsync<int>(world, id, "version")).ShouldBe(1);
    }

    /// <summary>
    /// An active member of the test's org with one built-in role, or none when <paramref name="roleCode"/> is null:
    /// a member with no role holds no permission at all, which no built-in role is (every one holds something).
    /// </summary>
    private async Task<Action<HttpRequestHeaders>> MemberAsync(World world, string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var sql = $"""
                   insert into public."user" (id, email) values ('{user}', 'member-{user:N}@example.test');
                   insert into public.membership (id, org_id, user_id, invited_email, status)
                   values (gen_random_uuid(), '{world.Org}', '{user}', 'member-{user:N}@example.test', 'active');
                   """;
        if (roleCode is not null)
        {
            sql += $"""

                    insert into public.membership_role (id, org_id, membership_id, role_id)
                    select gen_random_uuid(), '{world.Org}', m.id, r.id
                      from public.membership m, public.role r
                     where m.org_id = '{world.Org}' and m.user_id = '{user}'
                       and r.code = '{roleCode}' and r.org_id is null and r.is_builtin;
                    """;
        }

        await _fixture.ExecuteAsync(world.Database, sql);

        var roles = await _fixture.ScalarAsync<long>(
            world.Database,
            $"""
             select count(*) from public.membership_role mr
               join public.membership m on m.id = mr.membership_id
              where m.org_id = '{world.Org}' and m.user_id = '{user}'
             """);
        roles.ShouldBe(roleCode is null ? 0 : 1, "the member must hold exactly the roles the case assumes");

        return world.Caller(user);
    }
}
