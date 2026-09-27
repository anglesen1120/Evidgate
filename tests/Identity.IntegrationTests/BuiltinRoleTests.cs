using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.2.1-T2.2 / AC6 / AC7 on the real routes: the flagged custom-role commands
/// (<c>POST</c>/<c>PATCH /v1/roles</c>) against the migrated catalogue — 403 while
/// <c>custom_roles_enabled</c> is off (the V1 state, RECOMMENDATIONS 02-Q3), 409
/// <c>builtin_role_immutable</c> for a catalogue role whatever the flag says, and
/// 422 <c>unknown_permission</c> / <c>permission_not_assignable</c> for a code the
/// catalogue refuses (T79). The writes travel the module's EF model, so a create
/// that answers 201 has really inserted the role and its grants.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class BuiltinRoleTests : CatalogueTestBase
{
    /// <summary>snake_case wire → the module's public DTOs (00 §7 API JSON).</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    public BuiltinRoleTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>AC6: a built-in role is immutable — 409, and it is refused before the flag is even consulted.</summary>
    [Fact]
    public async Task Patch_Returns_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var viewerId = await IdentityTestData.RoleIdAsync(Fixture, org, "viewer", Token);

        await using var factory = Flagless(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PatchRoleAsync(client, viewerId, new UpdateRoleRequest("Read only", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertErrorCodeAsync(response, "builtin_role_immutable");
    }

    /// <summary>AC6: a role this tenant cannot see is 404, never another org's role (RLS decides).</summary>
    [Fact]
    public async Task Patch_Another_Orgs_Role_Is_404()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);

        Guid roleId;
        await using (var owner = WithCustomRoles(Fixture, otherOrg))
        {
            using var creator = owner.CreateClient();
            roleId = (await CreateRoleAsync(creator, "night_shift", "Night shift", [Permissions.Project.Read])).Id;
        }

        await using var factory = Flagless(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PatchRoleAsync(client, roleId, new UpdateRoleRequest("Anything", null));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertErrorCodeAsync(response, "not_found");
    }

    /// <summary>AC6: an id that names no role anywhere reads as the same 404 as another tenant's.</summary>
    [Fact]
    public async Task Patch_Unknown_Role_Is_404()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = Flagless(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PatchRoleAsync(client, Guid.CreateVersion7(), new UpdateRoleRequest("Anything", null));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertErrorCodeAsync(response, "not_found");
    }

    /// <summary>
    /// 02.2.1-T2.2 "flag-off create → 403": the route ships in V1 and says so, rather
    /// than disappearing and answering 404 for a documented path.
    /// </summary>
    [Fact]
    public async Task Create_With_The_Flag_Off_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = Flagless(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest("night_shift", "Night shift", [Permissions.Project.Read]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, "forbidden");
    }

    /// <summary>
    /// AC7 / T79: with the flag on, a code a member principal must never hold is
    /// refused 422 <c>permission_not_assignable</c>, and no role row is written.
    /// </summary>
    [Fact]
    public async Task Create_With_A_NonAssignable_Code_Is_422()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest("ledger_reader", "Ledger reader", [Permissions.Project.Read, Permissions.Billing.Read]));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await AssertErrorCodeAsync(response, "permission_not_assignable");

        await AssertNoCustomRoleAsync(org, "ledger_reader");
    }

    /// <summary>§5: a code the catalogue does not declare is 422 <c>unknown_permission</c>.</summary>
    [Fact]
    public async Task Create_With_An_Unknown_Code_Is_422()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest("odd_role", "Odd", [Permissions.Project.Read, "made_up:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await AssertErrorCodeAsync(response, "unknown_permission");

        await AssertNoCustomRoleAsync(org, "odd_role");
    }

    /// <summary>
    /// §5: a malformed body never opens a transaction — the edge checks answer 422
    /// <c>request_invalid</c> with the offending field.
    /// </summary>
    [Fact]
    public async Task Create_With_A_Malformed_Body_Is_422()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        var bodies = new[]
        {
            new CreateRoleRequest("Night-Shift", "Night shift", [Permissions.Project.Read]),
            new CreateRoleRequest("night_shift", string.Empty, [Permissions.Project.Read]),
            new CreateRoleRequest("night_shift", "Night shift", ["NOT A PERMISSION"]),
        };

        foreach (var body in bodies)
        {
            using var response = await PostRoleAsync(client, body);

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            await AssertErrorCodeAsync(response, "request_invalid");
        }
    }

    /// <summary>
    /// AC6 read side of the write: a custom role may not shadow a built-in code —
    /// the built-ins are the 00 §2 contract the org's roles are read against.
    /// </summary>
    [Fact]
    public async Task Create_With_A_Builtin_Code_Is_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest("viewer", "Fake viewer", [Permissions.Project.Read]));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertErrorCodeAsync(response, "builtin_role_immutable");
    }

    /// <summary>
    /// The happy path of §5 with the flag on: 201 <c>RoleView</c>, and the role and
    /// its grants are really in the database (the EF write model, C4).
    /// </summary>
    [Fact]
    public async Task Create_With_The_Flag_On_Creates_The_Role_And_Its_Grants()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest(
                "night_shift",
                "Night shift",
                [Permissions.Project.Read, Permissions.Run.Read, Permissions.Project.Read]));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<RoleView>(Wire, Token);

        created!.Id.ShouldNotBe(Guid.Empty);
        created.OrgId.ShouldBe(org);
        created.Code.ShouldBe("night_shift");
        created.Name.ShouldBe("Night shift");
        created.IsBuiltin.ShouldBeFalse();
        created.Version.ShouldBe(1);

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, created.Id, Token))
            .ShouldBe([Permissions.Project.Read, Permissions.Run.Read], ignoreOrder: true);

        // A second create of the same code is the unique (org_id, code) conflict, not
        // a 500 and not a second row.
        using var duplicate = await PostRoleAsync(client, new CreateRoleRequest("night_shift", "Another", []));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertErrorCodeAsync(duplicate, "role_code_taken");
    }

    /// <summary>
    /// 00 §9.1 / TL T11: the route requires an <c>Idempotency-Key</c>, and the C2
    /// idempotency stage is what says so — a create without one is 400
    /// <c>request_invalid</c>, not a silently non-idempotent write.
    /// </summary>
    [Fact]
    public async Task Create_Without_An_Idempotency_Key_Is_400()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/v1/roles",
            new CreateRoleRequest("night_shift", "Night shift", [Permissions.Project.Read]),
            idempotencyKey: null,
            includeIdempotencyKey: false);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertErrorCodeAsync(response, "request_invalid");
    }

    /// <summary>
    /// 00 §9.1 / TL T11: a retried create carrying the same <c>Idempotency-Key</c>
    /// replays the stored 201 and the same role id instead of colliding with its own
    /// <c>unique (org_id, code)</c> — the reason the key is required on this route.
    /// </summary>
    [Fact]
    public async Task Create_With_The_Same_Idempotency_Key_Replays_The_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        var key = "role-create-" + Guid.CreateVersion7().ToString("N");
        var body = new CreateRoleRequest("night_shift", "Night shift", [Permissions.Project.Read]);

        using var first = await PostRoleAsync(client, body, key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await first.Content.ReadFromJsonAsync<RoleView>(Wire, Token);

        using var replay = await PostRoleAsync(client, body, key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replayed = await replay.Content.ReadFromJsonAsync<RoleView>(Wire, Token);

        replayed!.Id.ShouldBe(created!.Id);
        (await IdentityTestData.RolePermissionsAsync(Fixture, org, created.Id, Token))
            .ShouldBe([Permissions.Project.Read], ignoreOrder: true);
    }

    /// <summary>
    /// 00 §9.1: the same <c>Idempotency-Key</c> with a DIFFERENT payload is 409
    /// <c>idempotency_conflict</c>, not a second role — the other half of the claim:
    /// the stage both replays a matching retry and refuses a mismatched one.
    /// </summary>
    [Fact]
    public async Task Create_With_The_Same_Key_And_A_Different_Body_Is_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        var key = "role-create-" + Guid.CreateVersion7().ToString("N");

        using var first = await PostRoleAsync(
            client,
            new CreateRoleRequest("night_shift", "Night shift", [Permissions.Project.Read]),
            key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var conflict = await PostRoleAsync(
            client,
            new CreateRoleRequest("day_shift", "Day shift", [Permissions.Project.Read]),
            key);

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertErrorCodeAsync(conflict, "idempotency_conflict");
        await AssertNoCustomRoleAsync(org, "day_shift");
    }

    /// <summary>
    /// §5 with the flag on: a PATCH renames the role and ADDS grants — the V1 shape
    /// (0010 revokes DELETE on <c>role_permission</c>, so a set can only grow).
    /// </summary>
    [Fact]
    public async Task Patch_With_The_Flag_On_Renames_And_Adds_Grants()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = WithCustomRoles(Fixture, org);
        using var client = factory.CreateClient();

        var created = await CreateRoleAsync(client, "night_shift", "Night shift", [Permissions.Project.Read]);

        using var response = await PatchRoleAsync(
            client,
            created.Id,
            new UpdateRoleRequest("Night shift (renamed)", [Permissions.Run.Read, Permissions.Project.Read]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<RoleView>(Wire, Token);

        updated!.Name.ShouldBe("Night shift (renamed)");
        updated.Version.ShouldBe(2, "the DB trigger owns the increment (00 §7)");

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, created.Id, Token))
            .ShouldBe([Permissions.Project.Read, Permissions.Run.Read], ignoreOrder: true);
    }

    /// <summary>
    /// The flag gates changes, not just creation: the same role a flag-on host
    /// created is refused 403 by a flag-off host (RECOMMENDATIONS 02-Q3).
    /// </summary>
    [Fact]
    public async Task Patch_Of_A_Custom_Role_With_The_Flag_Off_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        Guid roleId;
        await using (var enabled = WithCustomRoles(Fixture, org))
        {
            using var creator = enabled.CreateClient();
            roleId = (await CreateRoleAsync(creator, "night_shift", "Night shift", [Permissions.Project.Read])).Id;
        }

        await using var disabled = Flagless(Fixture, org);
        using var client = disabled.CreateClient();

        using var response = await PatchRoleAsync(client, roleId, new UpdateRoleRequest("Renamed", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, "forbidden");
    }

    /// <summary>
    /// AC4/AC6 together, without a substituted evaluator: a real <c>tenant_admin</c>
    /// member (the 00 §2 role that holds <c>role:write</c>) is authorized by the
    /// module's own evaluator over a seeded membership, and the built-in role it
    /// addresses is still 409.
    /// </summary>
    [Fact]
    public async Task Patch_Of_A_Builtin_Role_As_A_Real_Tenant_Admin_Is_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var viewerRoleId = await IdentityTestData.RoleIdAsync(Fixture, org, "viewer", Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(admin),
                CustomRolesEnabled: true));
        using var client = factory.CreateClient();

        using var response = await PatchRoleAsync(client, viewerRoleId, new UpdateRoleRequest("Read only", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertErrorCodeAsync(response, "builtin_role_immutable");
    }

    /// <summary>
    /// A <c>viewer</c> member (no <c>role:write</c>) hitting the write route through
    /// the real evaluator is 403 <c>forbidden</c> — AC3 on the role endpoints.
    /// </summary>
    [Fact]
    public async Task Create_As_A_Viewer_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var viewer = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(viewer),
                CustomRolesEnabled: true));
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest("night_shift", "Night shift", [Permissions.Project.Read]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, "forbidden");
    }

    /// <summary>
    /// W7-SEC-02 (00 §2): <c>CreateRole</c> refuses any code the caller does not hold, so
    /// a <c>tenant_admin</c> — which holds <c>role:write</c> — cannot write itself (or
    /// anyone) the <c>artifact:read_sensitive</c> it never has. 403 <c>forbidden</c>, the
    /// code the route's 403 response already carries, and no role row.
    /// </summary>
    [Fact]
    public async Task Create_With_A_Permission_The_Caller_Does_Not_Hold_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(admin),
                CustomRolesEnabled: true));
        using var client = factory.CreateClient();

        using var response = await PostRoleAsync(
            client,
            new CreateRoleRequest("night_shift", "Night shift", [Permissions.Artifact.ReadSensitive]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, "forbidden");
        await AssertNoCustomRoleAsync(org, "night_shift");
    }

    /// <summary>
    /// W7-SEC-02 on PATCH: adding a grant the caller does not hold is the same escalation
    /// as creating the role with it, and it is the same 403. The role is seeded as the
    /// owner (creating one needs the flag and passes the same rule), the caller is the
    /// real <c>tenant_admin</c>, and the refused code leaves the grant set untouched —
    /// while a code the caller does hold is added afterwards, so the rule is a subset
    /// check and not a blanket refusal of the route.
    /// </summary>
    [Fact]
    public async Task Patch_With_A_Permission_The_Caller_Does_Not_Hold_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var roleId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [("id", roleId), ("org_id", org), ("code", "night_shift"), ("name", "Night shift")],
            Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(admin),
                CustomRolesEnabled: true));
        using var client = factory.CreateClient();

        using var refused = await PatchRoleAsync(
            client,
            roleId,
            new UpdateRoleRequest(null, [Permissions.Artifact.ReadSensitive]));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(refused, "forbidden");

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, roleId, Token))
            .ShouldBeEmpty("the refused patch wrote no grant");

        using var accepted = await PatchRoleAsync(
            client,
            roleId,
            new UpdateRoleRequest(null, [Permissions.Project.Read]));

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, roleId, Token))
            .ShouldBe([Permissions.Project.Read], ignoreOrder: true);
    }

    /// <summary>A host with the V1 default: custom roles off, a caller whose permission is granted.</summary>
    private static IdentityApiFactory Flagless(DatabaseFixture fixture, Guid org) =>
        IdentityApiFactory.Start(
            fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(Guid.CreateVersion7()), Evaluator: new GrantAllPermissionEvaluator()));

    /// <summary>A host with <c>custom_roles_enabled</c> on — the V2 editor's shape.</summary>
    private static IdentityApiFactory WithCustomRoles(DatabaseFixture fixture, Guid org) =>
        IdentityApiFactory.Start(
            fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(Guid.CreateVersion7()),
                Evaluator: new GrantAllPermissionEvaluator(),
                CustomRolesEnabled: true));

    private static async Task<RoleView> CreateRoleAsync(
        HttpClient client,
        string code,
        string name,
        IReadOnlyList<string> permissions)
    {
        using var response = await PostRoleAsync(client, new CreateRoleRequest(code, name, permissions));

        return (await response.Content.ReadFromJsonAsync<RoleView>(Wire, TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// A create carrying an <c>Idempotency-Key</c> (00 §9.1, TL T11 — the route
    /// requires it), so a case can pin the replay behaviour.
    /// </summary>
    private static Task<HttpResponseMessage> PostRoleAsync(
        HttpClient client,
        CreateRoleRequest body,
        string? idempotencyKey = null) =>
        SendAsync(client, HttpMethod.Post, "/v1/roles", body, idempotencyKey);

    /// <summary>A patch carrying an <c>Idempotency-Key</c>, for the same reason.</summary>
    private static Task<HttpResponseMessage> PatchRoleAsync(
        HttpClient client,
        Guid roleId,
        UpdateRoleRequest body,
        string? idempotencyKey = null) =>
        SendAsync(client, HttpMethod.Patch, $"/v1/roles/{roleId}", body, idempotencyKey);

    private static async Task<HttpResponseMessage> SendAsync<TBody>(
        HttpClient client,
        HttpMethod method,
        string path,
        TBody body,
        string? idempotencyKey,
        bool includeIdempotencyKey = true)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Wire),
        };

        if (includeIdempotencyKey)
        {
            // A fresh key per request is the realistic client behaviour (00 §9.1);
            // the cases that pin replay or the missing key pass one explicitly.
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key",
                idempotencyKey ?? Guid.CreateVersion7().ToString("N"));
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Renders the failure the way 00 §9.2 requires and asserts its code.</summary>
    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var envelope = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        envelope.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(expected);
    }

    /// <summary>No rejected command may leave a role behind (the C2 transaction's rollback).</summary>
    private async Task AssertNoCustomRoleAsync(Guid org, string code)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);

        var count = await scope.Db.ScalarAsync<long>(
            "select count(*) from public.role where org_id is not null and code = @code",
            new { code },
            Token);

        count.ShouldBe(0);
    }
}
