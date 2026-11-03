using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// AC6 (read side) / 00 §9.1 / 00 §9.2: the real Api composition answers
/// <c>GET /v1/permissions</c> and <c>GET /v1/roles</c> with the seeded catalogue, in
/// the cursor-page shape, under the C2 pipeline — and refuses without a permission or
/// with an out-of-contract parameter.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CatalogueEndpointTests : CatalogueTestBase
{
    /// <summary>snake_case wire → the module's public DTOs (00 §7 API JSON).</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    public CatalogueEndpointTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>AC6: a member reads the whole seeded catalogue from the documented route.</summary>
    [Fact]
    public async Task Permissions_Route_Returns_The_Seeded_Catalogue()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions?limit=200", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // HTTP header names are case-insensitive; the host writes `X-Request-ID`.
        response.Headers.ShouldContain(header =>
            header.Key.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase));

        var body = await response.Content.ReadAsStringAsync(Token);
        var page = JsonSerializer.Deserialize<PermissionListResponse>(body, Wire)!;

        page.Items.Select(item => item.Code).ShouldBe(Catalogue.Codes);
        page.NextCursor.ShouldBeNull("the whole catalogue fits one 00 §9.1 page");

        var file = Catalogue.Document.Permissions.ToDictionary(permission => permission.Code, StringComparer.Ordinal);
        foreach (var item in page.Items)
        {
            item.Resource.ShouldBe(file[item.Code].Resource);
            item.Sensitive.ShouldBe(file[item.Code].Sensitive);
            item.Reserved.ShouldBe(file[item.Code].Reserved);
            item.TenantAssignable.ShouldBe(file[item.Code].TenantAssignable);
        }

        // The 00 §9.1 envelope keys, on the raw body (the DTO would hide a rename).
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("items", out _).ShouldBeTrue();
        document.RootElement.TryGetProperty("next_cursor", out _).ShouldBeTrue();
    }

    /// <summary>
    /// 00 §9.1: <c>?limit=</c> bounds the page and <c>?cursor=</c> continues it — the
    /// catalogue's cursor is the last code, and the pages do not overlap.
    /// </summary>
    [Fact]
    public async Task Permissions_Route_Pages_On_The_Code_Cursor()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var first = JsonSerializer.Deserialize<PermissionListResponse>(
            await client.GetStringAsync("/v1/permissions?limit=5", Token), Wire)!;

        first.Items.Count.ShouldBe(5);
        first.Items.Select(item => item.Code).ShouldBe(Catalogue.Codes.Take(5));
        first.NextCursor.ShouldBe(Catalogue.Codes[4]);

        var second = JsonSerializer.Deserialize<PermissionListResponse>(
            await client.GetStringAsync(
                $"/v1/permissions?limit=5&cursor={Uri.EscapeDataString(first.NextCursor!)}",
                Token),
            Wire)!;

        second.Items.Select(item => item.Code).ShouldBe(Catalogue.Codes.Skip(5).Take(5));
        second.Items.Select(item => item.Code).ShouldNotContain(code => first.Items.Any(item => item.Code == code));
    }

    /// <summary>
    /// AC6: the roles route returns the nine built-in roles of 00 §2 and never another
    /// org's custom role (catalogue RLS, 02 §4.5) — while the caller's own org role is
    /// visible to it.
    /// </summary>
    [Fact]
    public async Task Roles_Route_Returns_The_BuiltIns_And_Hides_Other_Orgs()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var other = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [
                ("id", Guid.CreateVersion7()), ("org_id", other), ("code", "foreign_role"), ("name", "Foreign"),
            ],
            Token);

        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [
                ("id", Guid.CreateVersion7()), ("org_id", org), ("code", "own_role"), ("name", "Own"),
            ],
            Token);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/roles?limit=200", Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = JsonSerializer.Deserialize<RoleListResponse>(await response.Content.ReadAsStringAsync(Token), Wire)!;

        page.Items.Select(role => role.Code).ShouldContain("tenant_admin");
        page.Items.Select(role => role.Code).ShouldContain("own_role");
        page.Items.Select(role => role.Code).ShouldNotContain("foreign_role");

        page.Items.Where(role => role.IsBuiltin).Select(role => role.Code).Order(StringComparer.Ordinal)
            .ShouldBe(Catalogue.RoleCodes);

        page.Items.Single(role => role.Code == "own_role").OrgId.ShouldBe(org);
        page.NextCursor.ShouldBeNull();
    }

    /// <summary>00 §9.1's cap is a contract, not a hint: an out-of-range page size is 422.</summary>
    [Fact]
    public async Task Out_Of_Range_Limit_Is_422()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        foreach (var limit in new[] { "0", "201", "not-a-number" })
        {
            using var response = await client.GetAsync($"/v1/permissions?limit={limit}", Token);

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
        }
    }

    /// <summary>A cursor this service did not issue is a client error, never a different page.</summary>
    [Theory]
    [InlineData("/v1/roles?cursor=not-a-cursor")]
    [InlineData("/v1/permissions?cursor=not-a-cursor")]
    [InlineData("/v1/permissions?cursor=zzz")]
    public async Task Malformed_Cursor_Is_422(string path)
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
        envelope.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "cursor");
    }

    /// <summary>
    /// S2/C2 on a real route: authorization runs in the pipeline, so the production
    /// deny-all default (no authentication exists yet) answers 403 <c>forbidden</c> —
    /// the catalogue is not readable without <c>role:read</c>.
    /// </summary>
    [Fact]
    public async Task Denied_Permission_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org, grantAll: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");
    }
}
