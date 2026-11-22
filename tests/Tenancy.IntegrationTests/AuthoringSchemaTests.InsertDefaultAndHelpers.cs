using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Migrator;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringSchemaTests
{
    // ------------------------------------------------------------------ case prefix: insert default (Q1)

    /// <summary>
    /// PANEL-CHOICE Q1: the BEFORE INSERT trigger fills a NULL prefix through the same stem+dedupe
    /// function as the backfill, so an INSERT with only <c>(id, org_id, name, slug)</c> — DevSeed and
    /// every existing test — keeps working and gets a well-formed, per-org unique value.
    /// </summary>
    [Fact]
    public async Task CasePrefix_InsertDefault_IsUniquePerOrg()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var acme = await SeedOrgAsync(database, "acme");
        var globex = await SeedOrgAsync(database, "globex");

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // Two projects of one org whose slugs share a stem, and one project of another org with
            // the same slug as the first — the prefix is unique per org, never globally.
            var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await seed.ExecuteAsync(
                InsertDatedProjectSql,
                DatedProjectParameters(Guid.CreateVersion7(), acme, "Checkout", "checkout", baseTime));
            await seed.ExecuteAsync(
                InsertDatedProjectSql,
                DatedProjectParameters(Guid.CreateVersion7(), acme, "Check out", "check-out", baseTime.AddMinutes(1)));
            await seed.ExecuteAsync(
                InsertDatedProjectSql,
                DatedProjectParameters(Guid.CreateVersion7(), globex, "Checkout", "checkout", baseTime));
            await seed.CommitAsync();
        }

        (await _fixture.ColumnAsync<string>(
            database,
            $"select slug || ':' || case_prefix from public.project where org_id = '{acme}' order by created_at, id"))
            .ShouldBe(["checkout:CHEC", "check-out:CHEC2"]);
        (await _fixture.ColumnAsync<string>(
            database,
            $"select case_prefix from public.project where org_id = '{globex}'"))
            .ShouldBe(["CHEC"], "another org may hold the same prefix");

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.project where org_id in ('{acme}', '{globex}') "
            + $"and (case_prefix is null or case_prefix !~ '{PrefixFormat}')"))
            .ShouldBe(0);
    }

    /// <summary>
    /// The write model maps <c>case_prefix</c> store-generated (0039 / OI-13-101), so the project
    /// create path needs no change: EF omits the column, the BEFORE INSERT trigger fills it, and EF
    /// reads the value back through RETURNING inside the same tenant scope
    /// (<c>WriteModelMappingTests.ModelMatchesSchema</c> pins the mapping itself, this test pins the
    /// behaviour a wrong mapping would break — a required, non-generated shadow property would make
    /// every insert fail with a NULL violation).
    /// </summary>
    [Fact]
    public async Task CasePrefix_EfProjectInsert_ReadsTriggerValue()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, _) = await SeedTenantAsync(database, "acme", "checkout");

        await using var source = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var scopes = new NpgsqlTenantScopeFactory(source);
        await using var scope = await scopes.BeginAsync(org, cancellationToken: TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<TenancyWriteModelContext>()
            .UseNpgsql(scope.Connection)
            .AddInterceptors(new TenantCommandInterceptor(scopes))
            .UseTenantModel(typeof(OrganizationConfiguration).Assembly)
            .Options;

        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            OrgId = org,
            Name = "EF project",
            Slug = "ef-project",
            CreatedAt = at,
            UpdatedAt = at,
        };

        await using (var context = new TenancyWriteModelContext(options))
        {
            await context.Database.UseTransactionAsync(scope.Transaction, TestContext.Current.CancellationToken);
            await context.Set<Project>().AddAsync(project, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await scope.CommitAsync(TestContext.Current.CancellationToken);

        // "ef-project" -> "EFPR": the trigger filled the column EF omitted, and the seeded project in
        // this org already holds "CHEC", so nothing collides.
        (await _fixture.ColumnAsync<string>(
            database,
            $"select case_prefix from public.project where id = '{project.Id}'"))
            .ShouldBe(["EFPR"]);
    }

    /// <summary>
    /// An explicit prefix is kept byte-for-byte (the trigger only fills a NULL): the OI-13-101
    /// override a tenant admin will set is never recomputed from the slug, and the format check and
    /// the per-org unique index still refuse an invalid or a duplicate value.
    /// </summary>
    [Fact]
    public async Task CasePrefix_InsertExplicit_IsKeptVerbatim()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // The slug would stem to SHOP; the stored prefix must stay ZULU.
            await seed.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug, case_prefix) values (@id, @org_id, @name, @slug, 'ZULU')",
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("name", "Shop"),
                ("slug", "shop"));
            await seed.CommitAsync();
        }

        (await _fixture.ColumnAsync<string>(database, $"select case_prefix from public.project where org_id = '{org}'"))
            .ShouldBe(["ZULU"]);

        var duplicate = await CaptureOwnerErrorAsync(
            database,
            $"insert into public.project (id, org_id, name, slug, case_prefix) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', 'Other', 'other', 'ZULU')");
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("project_org_case_prefix_uq");

        var malformed = await CaptureOwnerErrorAsync(
            database,
            $"insert into public.project (id, org_id, name, slug, case_prefix) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', 'Lower', 'lower', 'zulu')");
        malformed.SqlState.ShouldBe("23514");
        malformed.ConstraintName.ShouldBe("project_case_prefix_format");
    }

    /// <summary>
    /// The <c>pg_advisory_xact_lock</c> half of Q1: two concurrent creates in one org with slugs that
    /// stem to the same value serialise on the per-org lock, so the second sees the first's committed
    /// prefix and takes the next candidate — no 23505 on <c>project_org_case_prefix_uq</c>.
    /// </summary>
    [Fact]
    public async Task CasePrefix_ConcurrentInserts_GetDistinctPrefixes()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");
        var connectionString = _fixture.OwnerConnectionString(database);
        var token = TestContext.Current.CancellationToken;

        await using var winner = new NpgsqlConnection(connectionString);
        await winner.OpenAsync(token);
        await using var winnerTransaction = await winner.BeginTransactionAsync(token);

        // The first create takes the org's advisory lock and holds it until commit.
        (await ExecuteAsync(winner, winnerTransaction, InsertProjectSql, ProjectParameters(Guid.CreateVersion7(), org, "Shop", "shop")))
            .ShouldBe(1);

        await using var loser = new NpgsqlConnection(connectionString);
        await loser.OpenAsync(token);
        await using var loserTransaction = await loser.BeginTransactionAsync(token);

        // `sh-op` stems to the same SHOP, so this create must wait for the lock instead of racing.
        var loserInsert = ExecuteAsync(loser, loserTransaction, InsertProjectSql, ProjectParameters(Guid.CreateVersion7(), org, "Sh op", "sh-op"));
        await Task.Delay(TimeSpan.FromMilliseconds(400), token);
        loserInsert.IsCompleted.ShouldBeFalse("the second create waits on the per-org advisory lock");

        await winnerTransaction.CommitAsync(token);
        await loserInsert.WaitAsync(TimeSpan.FromSeconds(30), token);
        await loserTransaction.CommitAsync(token);

        (await _fixture.ColumnAsync<string>(
            database,
            $"select slug || ':' || case_prefix from public.project where org_id = '{org}' order by created_at, id"))
            .ShouldBe(["shop:SHOP", "sh-op:SHOP2"]);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An INSERT of a project with only <c>(id, org_id, name, slug)</c>: the trigger fills the prefix.</summary>
    private const string InsertProjectSql =
        "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)";

    /// <summary>The same, with an explicit <c>created_at</c>/<c>updated_at</c> so the order is pinned.</summary>
    private const string InsertDatedProjectSql =
        "insert into public.project (id, org_id, name, slug, created_at, updated_at) "
        + "values (@id, @org_id, @name, @slug, @created_at, @created_at)";

    /// <summary>The parameters of <see cref="InsertProjectSql"/> — the 4-column INSERT.</summary>
    private static (string Name, object Value)[] ProjectParameters(Guid id, Guid org, string name, string slug) =>
    [
        ("id", id),
        ("org_id", org),
        ("name", name),
        ("slug", slug),
    ];

    /// <summary>The parameters of <see cref="InsertDatedProjectSql"/>, with the pinned timestamps.</summary>
    private static (string Name, object Value)[] DatedProjectParameters(
        Guid id,
        Guid org,
        string name,
        string slug,
        DateTimeOffset createdAt) =>
    [
        ("id", id),
        ("org_id", org),
        ("name", name),
        ("slug", slug),
        ("created_at", createdAt),
    ];

    /// <summary>An org and a project in it, written as the migration role (the owner policy permits it).</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = await SeedOrgAsync(database, orgSlug);
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(InsertProjectSql, ProjectParameters(project, org, projectSlug, projectSlug));
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>One organization row, written as the migration role; the slug is unique per run.</summary>
    private async Task<Guid> SeedOrgAsync(string database, string slug)
    {
        var org = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", slug),
            ("slug", unique));
        await seed.CommitAsync();

        return org;
    }

    /// <summary>
    /// What a deploy run over the repository's own migration directory must satisfy, independent of
    /// how many files exist after <paramref name="migrationFile"/>: the file is applied, the applied
    /// set is contiguous (nothing at or before it is re-applied) and it is applied in lexical order.
    /// </summary>
    private static void AssertDeployApplied(string migrationFile, MigrationRun run)
    {
        run.Result.AppliedNow.ShouldContain(migrationFile);
        run.Result.AppliedNow
            .Where(name => string.CompareOrdinal(name, migrationFile) < 0)
            .ShouldBeEmpty($"no file at or before {migrationFile} may be re-applied; applied: {string.Join(", ", run.Result.AppliedNow)}");
        run.Result.AppliedNow.Order(StringComparer.Ordinal).ToArray()
            .ShouldBe(run.Result.AppliedNow.ToArray(), "the pending files are applied in lexical order");
    }

    /// <summary>A database brought to the head **before** 0039, so the backfill sees pre-existing rows.</summary>
    private async Task<DedicatedDatabase> BringToPreviousHeadAsync()
    {
        var database = await _fixture.CreateEmptyDedicatedDatabaseAsync();
        try
        {
            // Every migration file lexically before the one under test: the real expand path, and it
            // keeps working when later migrations are added to the repository.
            var prefixes = Directory.EnumerateFiles(PostgresTenancyFixture.RepoMigrationsPath, "*.sql")
                .Select(Path.GetFileName)
                .Where(name => string.CompareOrdinal(name, MigrationFile) < 0)
                .Select(name => name![..4])
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            using var history = TempMigrationDirectory.CreateFrom(PostgresTenancyFixture.RepoMigrationsPath, prefixes);
            var baselined = _fixture.ApplyMigrations(database, history.Path);
            baselined.ExitCode.ShouldBe(MigratorExitCodes.Success, baselined.Transcript);
            baselined.Result.AppliedNow.ShouldNotContain(MigrationFile);

            return database;
        }
        catch
        {
            // A failed arrange must not leave its scratch database behind for the rest of the run.
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// An index definition with the double quotes removed: <c>pg_get_indexdef</c> quotes reserved
    /// words such as <c>"position"</c>, and the assertions below compare columns, not quoting style.
    /// </summary>
    private static string Unquoted(string indexDefinition) =>
        indexDefinition.Replace("\"", string.Empty, StringComparison.Ordinal);

    /// <summary>An INSERT of a feature node; the caller supplies the position and the parent.</summary>
    private static string InsertNode(Guid id, Guid org, Guid project, Guid? parent, string kind, string title, int position) =>
        "insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position) "
        + $"values ('{id}', '{org}', '{project}', {(parent is null ? "null" : $"'{parent}'")}, '{kind}', '{title}', {position.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Runs one statement as the migration role inside a fresh transaction and returns the server error.</summary>
    private async Task<PostgresException> CaptureOwnerErrorAsync(string database, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>Runs one statement on an open connection and transaction.</summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
