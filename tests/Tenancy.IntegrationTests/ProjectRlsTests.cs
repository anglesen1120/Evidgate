using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC7 / FR-02-11 and 00 §5.4.7: as <c>evidgate_app</c> a tenant sees exactly its
/// own organization and projects, another org's id is indistinguishable from a
/// missing one (the database half of AC3), an unset tenant setting yields zero
/// rows, and a cross-tenant write is rejected by the <c>WITH CHECK</c> side of the
/// policy rather than by application code.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ProjectRlsTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ProjectRlsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The cross-tenant read/write check on <c>project</c> and <c>organization</c> as the app role.</summary>
    [Fact]
    public async Task AppRoleSeesOnlyItsOwnTenant()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();
        var projectA = Guid.CreateVersion7();
        var projectB = Guid.CreateVersion7();

        await using (var setup = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await setup.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", orgA),
                ("name", "Acme"),
                ("slug", "acme"));
            await setup.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", orgB),
                ("name", "Globex"),
                ("slug", "globex"));
            await setup.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", projectA),
                ("org_id", orgA),
                ("name", "Checkout"),
                ("slug", "checkout"));
            await setup.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", projectB),
                ("org_id", orgB),
                ("name", "Globex Web"),
                ("slug", "web"));
            await setup.CommitAsync();
        }

        // Scoped to A: A's rows, counted and read; B's project id is invisible.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgA))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(1);
            (await scope.ScalarAsync<long>("select count(*) from public.project")).ShouldBe(1);
            (await scope.ScalarAsync<Guid>("select id from public.project")).ShouldBe(projectA);

            // AC3 at the database level: another org's project id reads as missing.
            (await scope.ScalarAsync<long>(
                    "select count(*) from public.project where id = @id",
                    ("id", projectB)))
                .ShouldBe(0);

            // The tenant's own row is writable ...
            (await scope.ExecuteAsync(
                    "update public.project set name = @name where id = @id",
                    ("name", "Renamed"),
                    ("id", projectA)))
                .ShouldBe(1);

            // ... and the WITH CHECK side rejects a row for the other tenant
            // (42501: new row violates row-level security policy). Last statement
            // in this scope: a failed statement aborts the transaction.
            var denied = await scope.CaptureErrorAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", Guid.CreateVersion7()),
                ("org_id", orgB),
                ("name", "Smuggled"),
                ("slug", "smuggled"));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Scoped to B: B's rows only, so the same query returns a different tenant.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgB))
        {
            (await scope.ScalarAsync<Guid>("select id from public.project")).ShouldBe(projectB);
            (await scope.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(1);
        }

        // The audited operator path has its SELECT grant but no policy in this
        // task's migrations: without a support grant a scoped read returns zero
        // rows (TL T76, D1), and the role still cannot write.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database), orgA))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.project")).ShouldBe(0);
            (await scope.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(0);

            var denied = await scope.CaptureErrorAsync("delete from public.project");
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    /// <summary>AC7: with <c>evidgate.org_id</c> never set, both tables return zero rows on a populated database.</summary>
    [Fact]
    public async Task UnsetTenantSettingReturnsZeroRows()
    {
        var database = await _fixture.CreateDatabaseAsync();

        await using (var setup = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await setup.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", Guid.CreateVersion7()),
                ("name", "Acme"),
                ("slug", "acme"));
            await setup.CommitAsync();
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database));

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.project")).ShouldBe(0);
    }
}
