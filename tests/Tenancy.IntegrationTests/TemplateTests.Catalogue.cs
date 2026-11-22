using System.Net;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The row-level-security half of the template registry: built-ins reach every org, an org row reaches only
/// its org, and the application role can read but never write the table.</summary>
public sealed partial class TemplateTests
{
    [Fact]
    public async Task Template_CatalogueRls()
    {
        await using var world = await StartAsync();
        var builtIn = await SeedAsync(world, builtIn: true, createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var own = await SeedAsync(world, createdAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var inactive = await SeedAsync(world, active: false, createdAt: new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));

        var (otherUser, otherOrg) = await AddOrgAsync(world);
        var foreign = await SeedAsync(world, org: otherOrg, createdAt: new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero));

        var asA = await ListAsync(world);
        asA.Status.ShouldBe(HttpStatusCode.OK, asA.ErrorMessage);
        Ids(asA).ShouldBe([builtIn, own], "org A sees the built-in and its own active row, oldest first");

        var asB = await ListAsync(world, caller: World.CallerIn(otherUser, otherOrg));
        asB.Status.ShouldBe(HttpStatusCode.OK, asB.ErrorMessage);
        Ids(asB).ShouldBe([builtIn, foreign], "org B sees the same built-in plus its own row, never org A's");

        (await GetAsync(world, builtIn)).Status.ShouldBe(HttpStatusCode.OK, "a built-in is readable by every org");
        (await GetAsync(world, own)).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(world, foreign)).Status.ShouldBe(HttpStatusCode.NotFound, "another org's row is hidden by RLS");
        (await GetAsync(world, own, World.CallerIn(otherUser, otherOrg))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(world, inactive)).Status.ShouldBe(HttpStatusCode.NotFound, "an inactive row is not served");

        // The app role's privileges, one statement per transaction because a refused statement aborts its transaction.
        (await RefusalAsync(world,
            "insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules) values (gen_random_uuid(), @org, 'web', 't-app', 1, @body_ref, '{}'::jsonb)",
            ("org", world.Org), ("body_ref", BodyRef))).ShouldBe("42501", "evidgate_app cannot INSERT a template");
        (await RefusalAsync(world, "update public.template set active = false where id = @id", ("id", builtIn)))
            .ShouldBe("42501", "evidgate_app cannot UPDATE a template");
        (await RefusalAsync(world, "delete from public.template where id = @id", ("id", builtIn)))
            .ShouldBe("42501", "evidgate_app cannot DELETE a template");

        (await _fixture.ScalarAsync<bool>(world.Database, $"select active from public.template where id = '{builtIn}'"))
            .ShouldBeTrue("a refused write left the row untouched");
    }

    /// <summary>Runs one statement as <c>evidgate_app</c> in the org's tenant scope and returns its SQLSTATE.</summary>
    private async Task<string> RefusalAsync(World world, string sql, params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(sql, parameters);
        return error.SqlState;
    }
}
