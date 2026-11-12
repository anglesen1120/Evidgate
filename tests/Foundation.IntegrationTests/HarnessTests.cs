using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Foundation.IntegrationTests;

/// <summary>
/// 01.6.1-T2.1 (AC1) and T2.4 (AC3): the harness boots PostgreSQL 18 through
/// Testcontainers, brings the shared database to the head this build expects with
/// the real <c>Evidgate.Migrator</c>, and hands the tests the app role — never the
/// owner, never the container superuser.
/// </summary>
[Trait("Category", "Docker")]
public sealed class HarnessTests : TenantTest
{
    public HarnessTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC1: the shared database is at head. The journal is read as the app role,
    /// which holds only <c>SELECT</c> on it (0001), i.e. exactly the readiness path
    /// of 01.1.2-T1.3 — and the head is the value baked into the build, so a
    /// migration that lands without being applied fails here.
    /// </summary>
    [Fact]
    public async Task Fixture_MigratesToHead()
    {
        var head = await Fixture.JournalHeadAsync(Token);

        head.ShouldBe(DatabaseFixture.ExpectedHead);

        // The head names a real migration file, and every file of migrations/ is
        // journaled: a partially applied directory (a file the Migrator skipped)
        // fails here rather than in a later, confusing module test.
        File.Exists(Path.Combine(DatabaseFixture.MigrationsPath, DatabaseFixture.ExpectedHead))
            .ShouldBeTrue("the journal head must be one of the migration file names");

        var applied = await Fixture.JournalCountAsync(Token);
        applied.ShouldBe(Directory.GetFiles(DatabaseFixture.MigrationsPath, "*.sql").LongLength);
    }

    /// <summary>
    /// AC3: the harness runs tests as <c>evidgate_app</c>. Asserted on the shared
    /// data source and on a live tenant scope, because those are the two places a
    /// test actually gets a connection from.
    /// </summary>
    [Fact]
    public async Task Harness_UsesAppRole()
    {
        await using (var command = Fixture.AppDataSource.CreateCommand("select current_user"))
        {
            (await command.ExecuteScalarAsync(Token)).ShouldBe("evidgate_app");
        }

        await using (var command = Fixture.AppDataSource.CreateCommand("select current_setting('is_superuser')"))
        {
            (await command.ExecuteScalarAsync(Token)).ShouldBe("off");
        }

        // The tenant path borrows from the same pool, so it is app-role too.
        await using var scope = await Fixture.BeginTenantScopeAsync(Guid.CreateVersion7(), cancellationToken: Token);
        (await scope.ScalarAsync<string>("select current_user", Token)).ShouldBe("evidgate_app");
    }

    /// <summary>
    /// AC3: no Evidgate role may bypass RLS (DECISIONS F5). Two halves — the
    /// catalog, where a migration granting the app role <c>BYPASSRLS</c> would show
    /// up (a migration cannot set the flag on an existing role, but the superuser
    /// bootstrap could, and the convention test is the review gate the AC names);
    /// and the behaviour, where a row of another tenant stays invisible to the app
    /// role, which is what "no bypass" has to mean in practice.
    /// </summary>
    [Fact]
    public async Task AppRole_HasNoBypassRls()
    {
        await using (var connection = new NpgsqlConnection(Fixture.OwnerConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole
                  from pg_catalog.pg_roles
                 where rolname like 'evidgate\_%'
                 order by rolname
                """;

            var flags = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Token);

            while (await reader.ReadAsync(Token))
            {
                flags.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{reader.GetString(0)}: super={reader.GetBoolean(1)} bypassrls={reader.GetBoolean(2)} "
                        + $"createdb={reader.GetBoolean(3)} createrole={reader.GetBoolean(4)}"));
            }

            string[] expected =
            [
                "evidgate_app: super=False bypassrls=False createdb=False createrole=False",
                "evidgate_operator: super=False bypassrls=False createdb=False createrole=False",
                "evidgate_owner: super=False bypassrls=False createdb=False createrole=True",
                "evidgate_worker: super=False bypassrls=False createdb=False createrole=False",
                "evidgate_worker_sched: super=False bypassrls=False createdb=False createrole=False",
            ];

            flags.ShouldBe(expected);
        }

        // Behaviour: rows of another tenant are invisible to the app role even
        // though the owner just wrote them under its own permissive policy.
        var otherTenant = Guid.CreateVersion7();
        var mine = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.rls_probe (org_id, id, label) values (@org_id, @id, @label)",
            [("org_id", otherTenant), ("id", Guid.CreateVersion7()), ("label", "other tenant")],
            Token);
        await Fixture.SeedAsync(
            "insert into public.rls_probe (org_id, id, label) values (@org_id, @id, @label)",
            [("org_id", mine), ("id", Guid.CreateVersion7()), ("label", "my tenant")],
            Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(mine, cancellationToken: Token);
        (await scope.ScalarAsync<long>("select count(*) from public.rls_probe", Token)).ShouldBe(1L);

        await using var foreign = scope.Command("select count(*) from public.rls_probe where org_id = @org_id");
        foreign.Parameters.AddWithValue("org_id", otherTenant);
        ((long)(await foreign.ExecuteScalarAsync(Token))!).ShouldBe(0L);
    }
}
