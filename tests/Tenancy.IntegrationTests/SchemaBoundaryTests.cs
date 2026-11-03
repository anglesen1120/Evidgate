using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA additions for 02.1.1-T1 (independent of the author's suite): the input
/// boundaries the migrations 0004/0005 enforce (00 §5.7 S3), and the two tenant
/// escapes the row level security of those tables must keep closed — an empty
/// tenant setting must be as good as an unset one (00 §5.4.7), and no write may
/// move a row into, or create a row for, another tenant (00 §5.4.1/§5.4.6).
/// </summary>
[Trait("Category", "Docker")]
public sealed class SchemaBoundaryTests
{
    private readonly PostgresTenancyFixture _fixture;

    public SchemaBoundaryTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The D-A1 slug grammar <c>^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$</c> (0012):
    /// lowercase alphanumerics and interior hyphens only, 2–63 characters. Uppercase,
    /// a leading or trailing hyphen, underscores, spaces, punctuation and non-ASCII
    /// must all be a 422 at the edge, not a stored row — and must not reach a 409.
    /// </summary>
    [Fact]
    public async Task SlugGrammarBoundaries()
    {
        // The boundary slugs are fixed and the table is shared, so this class owns a migrated database.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        var accepted = new[]
        {
            "ab",
            "a-2",
            "a--b",
            "0000",
            new string('a', 63),
        };

        var rejected = new[]
        {
            "a",
            "AB",
            "aB",
            "-ab",
            "ab-",
            "a-",
            "a_b",
            "a b",
            "ab!",
            "a.b",
            "aé",
            new string('a', 64),
        };

        foreach (var slug in accepted)
        {
            (await InsertOrganizationAsync(database, slug)).ShouldBeNull($"slug '{slug}' is valid");
        }

        foreach (var slug in rejected)
        {
            var error = await InsertOrganizationAsync(database, slug);
            error.ShouldNotBeNull($"slug '{slug}' must be rejected");
            error!.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, $"slug '{slug}'");
        }
    }

    /// <summary>
    /// The name input limit (00 §5.7 S3): 1–128 characters. An empty string
    /// passes <c>not null</c>, so the length check is the only thing standing
    /// between the API and a nameless tenant row.
    /// </summary>
    [Fact]
    public async Task NameLengthBoundaries()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var slug = 0;

        foreach (var (name, expectAccepted) in new[]
                 {
                     (string.Empty, false),
                     (new string('x', 128), true),
                     (new string('x', 129), false),
                 })
        {
            var error = await InsertOrganizationAsync(database, Unique.Slug($"name-{slug++}"), name);
            if (expectAccepted)
            {
                error.ShouldBeNull($"a {name.Length}-character name is valid");
            }
            else
            {
                error.ShouldNotBeNull($"a {name.Length}-character name must be rejected");
                error!.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
        }
    }

    /// <summary>
    /// 00 §5.4.7 / TL T2: the helper maps the empty string to NULL, so a request
    /// that carries <c>evidgate.org_id = ''</c> — an unset claim serialized as an
    /// empty value, the classic way this bug arrives — sees zero rows instead of
    /// every tenant's rows.
    /// </summary>
    [Fact]
    public async Task EmptyTenantSettingReturnsZeroRows()
    {
        var seeded = await SeedTwoTenantsAsync();
        await using var database = seeded.Database;

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database));
        await scope.SetAsync("evidgate.org_id", string.Empty);

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.project")).ShouldBe(0);
    }

    /// <summary>
    /// A cross-tenant <c>update</c> is not rejected — the <c>USING</c> side of the
    /// policy simply hides the row, so it must affect exactly zero rows. A row count
    /// of 1 here would be a silent cross-tenant write. A cross-tenant (or own-row)
    /// <c>delete</c> is a different story since M1W3-05: the app role holds no
    /// DELETE at all, so it is refused with 42501 before RLS is consulted.
    /// </summary>
    [Fact]
    public async Task CrossTenantWritesAffectNoRows()
    {
        var seeded = await SeedTwoTenantsAsync();
        await using var database = seeded.Database;
        var (tenantA, tenantB) = (seeded.TenantA, seeded.TenantB);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), tenantA.OrgId))
        {
            (await scope.ExecuteAsync(
                    "update public.organization set name = 'hijacked' where id = @id",
                    ("id", tenantB.OrgId)))
                .ShouldBe(0);
            (await scope.ExecuteAsync(
                    "update public.project set name = 'hijacked' where id = @id",
                    ("id", tenantB.ProjectId)))
                .ShouldBe(0);

            // The tenant's own rows stay writable, so the zero counts above are the
            // policy and not a lost privilege.
            (await scope.ExecuteAsync(
                    "update public.organization set name = 'renamed' where id = @id",
                    ("id", tenantA.OrgId)))
                .ShouldBe(1);
            (await scope.ExecuteAsync(
                    "update public.project set name = 'renamed' where id = @id",
                    ("id", tenantA.ProjectId)))
                .ShouldBe(1);
        }

        // M1W3-05: the hard delete is a privilege error for the app role, on another
        // tenant's row and on its own alike. One scope per statement — an error aborts
        // its transaction, so a second one would report 25P02 instead of 42501.
        foreach (var (table, id) in new[] { ("organization", tenantB.OrgId), ("project", tenantB.ProjectId) })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), tenantA.OrgId);

            (await scope.CaptureErrorAsync(
                    $"delete from public.{table} where id = @id",
                    ("id", id)))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    /// <summary>
    /// The <c>WITH CHECK</c> side of the app policy: a tenant may not re-parent
    /// its own project into another org (42501), and may not insert a project or
    /// an organization for an id that belongs to another tenant.
    /// </summary>
    [Fact]
    public async Task WithCheckRejectsWritesIntoAnotherTenant()
    {
        var seeded = await SeedTwoTenantsAsync();
        await using var database = seeded.Database;
        var (tenantA, tenantB) = (seeded.TenantA, seeded.TenantB);

        // Re-parenting an own project into B: the USING side matches the row, the
        // WITH CHECK side rejects the new value.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), tenantA.OrgId))
        {
            var reparent = await scope.CaptureErrorAsync(
                "update public.project set org_id = @org_id where id = @id",
                ("org_id", tenantB.OrgId),
                ("id", tenantA.ProjectId));
            reparent.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // An organization row for another tenant's id.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), tenantA.OrgId))
        {
            var forged = await scope.CaptureErrorAsync(
                "insert into public.organization (id, name, slug) values (@id, 'Sneak', 'sneak')",
                ("id", tenantB.OrgId));
            forged.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // A project row for another tenant's org.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), tenantA.OrgId))
        {
            var smuggled = await scope.CaptureErrorAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, 'Smuggled', 'smuggled')",
                ("id", Guid.CreateVersion7()),
                ("org_id", tenantB.OrgId));
            smuggled.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private async Task<PostgresException?> InsertOrganizationAsync(string database, string slug, string? name = null)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        try
        {
            await scope.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", Guid.CreateVersion7()),
                ("name", name ?? "Acme"),
                ("slug", slug));
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }

    private Task<(DedicatedDatabase Database, TenantRow TenantA, TenantRow TenantB)> SeedTwoTenantsAsync() =>
        _fixture.StartDedicatedAsync(async database =>
        {
            var tenantA = new TenantRow(Guid.CreateVersion7(), Guid.CreateVersion7());
            var tenantB = new TenantRow(Guid.CreateVersion7(), Guid.CreateVersion7());
            var index = 0;

            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

            foreach (var tenant in new[] { tenantA, tenantB })
            {
                index++;
                await scope.ExecuteAsync(
                    "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                    ("id", tenant.OrgId),
                    ("name", "Tenant " + index.ToString(CultureInfo.InvariantCulture)),
                    ("slug", "tenant-" + index.ToString(CultureInfo.InvariantCulture)));
                await scope.ExecuteAsync(
                    "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                    ("id", tenant.ProjectId),
                    ("org_id", tenant.OrgId),
                    ("name", "Project " + index.ToString(CultureInfo.InvariantCulture)),
                    ("slug", "project-" + index.ToString(CultureInfo.InvariantCulture)));
            }

            await scope.CommitAsync();

            return (database, tenantA, tenantB);
        });

    internal sealed record TenantRow(Guid OrgId, Guid ProjectId);
}
