using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// <c>0110_identity_rate_limit_windows.sql</c> on a database at the previous head: the shared window table under
/// forced row level security with an owner-only policy, every runtime role without table privileges, the definer
/// executable only by the app role, and a second application of the same file changing nothing; then
/// <c>0112_rate_limit_acquire_cost.sql</c> adds the cost overload beside the cost-1 wrapper and leaves the table alone.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RateLimitWindowsMigrationTests
{
    private const string RateFile = "0110_identity_rate_limit_windows.sql";
    private const string CostFile = "0112_rate_limit_acquire_cost.sql";
    private const string Table = "public.rate_limit_window";
    private const string Function = "evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer)";
    private const string CostFunction = "evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer, integer)";

    private static readonly string[] RuntimeRoles =
        ["evidgate_app", "evidgate_operator", "evidgate_worker", "evidgate_worker_sched"];

    private readonly PostgresMigratorFixture _fixture;

    public RateLimitWindowsMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_AppliesOnThePreviousHead_AndAddsTheForcedWindowTable()
    {
        var database = await PreviousHeadAsync();

        (await PolicyCountAsync(database)).ShouldBe(0L);
        (await _fixture.TableExistsAsync(database, "rate_limit_window")).ShouldBeFalse();

        var applied = RunHead(database);

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([RateFile], "nothing before it is re-applied");
        (await _fixture.TableExistsAsync(database, "rate_limit_window")).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_indexes where schemaname = 'public' "
            + "and indexname = 'rate_limit_window_surface_window_start_idx'")).ShouldBe(1L);
        (await _fixture.ScalarAsync<bool>(
            database,
            "select relrowsecurity and relforcerowsecurity from pg_class "
            + "where oid = 'public.rate_limit_window'::regclass")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_Only_Policy_Names_The_Owner_And_No_Runtime_Role_Holds_Table_Privileges()
    {
        var database = await AppliedAsync();

        (await PolicyCountAsync(database)).ShouldBe(1L, "one policy and no catch-all");
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_policy p join pg_class c on c.oid = p.polrelid "
            + "where c.oid = '" + Table + "'::regclass "
            + "and (select rolname from pg_roles where oid = p.polroles[1]) = 'evidgate_owner'")).ShouldBe(1L);
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_policy p join pg_class c on c.oid = p.polrelid "
            + "where c.oid = '" + Table + "'::regclass and exists ("
            + "select 1 from unnest(p.polroles) r(role_oid) join pg_roles g on g.oid = r.role_oid "
            + "where g.rolname <> 'evidgate_owner')")).ShouldBe(0L, "no role other than the owner is named");

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from information_schema.role_table_grants "
            + "where table_schema = 'public' and table_name = 'rate_limit_window' "
            + "and grantee in ('evidgate_app', 'evidgate_operator', 'evidgate_worker', 'evidgate_worker_sched')"))
            .ShouldBe(0L);

        foreach (var role in RuntimeRoles)
        {
            (await HasTablePrivilegeAsync(database, role)).ShouldBeFalse($"{role} must hold no privilege on the window table");
        }

        (await PublicAclEntriesAsync(database, function: false)).ShouldBe(0L, "PUBLIC holds no table privilege either");
    }

    [Fact]
    public async Task Only_The_App_Role_May_Execute_The_Acquire_Function()
    {
        var database = await AppliedAsync();

        (await HasExecuteAsync(database, "evidgate_app")).ShouldBeTrue("the positive control: the runtime reaches the definer");
        (await HasExecuteAsync(database, "evidgate_owner")).ShouldBeTrue("the owner keeps its own function");
        foreach (var role in RuntimeRoles[1..])
        {
            (await HasExecuteAsync(database, role)).ShouldBeFalse($"{role} must not execute the definer");
        }

        (await PublicAclEntriesAsync(database, function: true)).ShouldBe(0L, "EXECUTE is revoked from PUBLIC");
    }

    [Fact]
    public async Task The_App_Role_Is_Neither_Owner_Nor_BypassRls_And_Reaches_Clients_Only_Through_The_Definer()
    {
        var database = await AppliedAsync();
        await _fixture.EnableApplicationLoginsAsync(database);

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_roles where rolname = 'evidgate_app' "
            + "and (rolsuper or rolbypassrls or rolcreaterole)")).ShouldBe(0L);
        (await _fixture.ScalarAsync<string>(
            database,
            "select pg_get_userbyid(relowner) from pg_class where oid = 'public.rate_limit_window'::regclass"))
            .ShouldBe("evidgate_owner");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var direct = connection.CreateCommand())
        {
            direct.CommandText = "select count(*) from public.rate_limit_window";
            var error = await Should.ThrowAsync<PostgresException>(
                async () => await direct.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var definer = connection.CreateCommand();
        definer.CommandText =
            "select allowed, retry_after_seconds from evidgate_resolve.rate_limit_acquire("
            + "'login', sha256('probe'::bytea), 1, 60, 100)";
        await using var reader = await definer.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeTrue();
        reader.GetInt32(1).ShouldBe(0);
    }

    [Fact]
    public async Task Applying_The_Same_File_Again_Changes_Nothing()
    {
        var database = await AppliedAsync();

        var again = RunHead(database);

        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBeEmpty("the journal skips an applied file");

        // The file's own guards must also hold when it is replayed outside the journal.
        await using (var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database)))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var replay = connection.CreateCommand();
            replay.CommandText = await File.ReadAllTextAsync(
                Path.Combine(PostgresMigratorFixture.RepoMigrationsPath, RateFile),
                TestContext.Current.CancellationToken);
            await replay.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await PolicyCountAsync(database)).ShouldBe(1L);
        (await AcquireFunctionCountAsync(database)).ShouldBe(1L);
        (await PublicAclEntriesAsync(database, function: false)).ShouldBe(0L);
        (await PublicAclEntriesAsync(database, function: true)).ShouldBe(0L);
    }

    [Fact]
    public async Task CostMigration_PreviousHead_AddsOverloadWithoutChangingTablePrivileges()
    {
        var database = await PreviousHeadAsync(CostFile);
        var tableAcl = await TableAclAsync(database);
        (await _fixture.ScalarAsync<bool>(
            database, "select to_regprocedure('" + Function + "') is not null")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database, "select to_regprocedure('" + CostFunction + "') is null")).ShouldBeTrue();

        var applied = RunHead(database, CostFile);

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([CostFile]);
        (await AcquireFunctionCountAsync(database)).ShouldBe(2L);
        (await _fixture.ScalarAsync<bool>(
            database, "select to_regprocedure('" + Function + "') is not null")).ShouldBeTrue("the wrapper stays callable");
        (await _fixture.ScalarAsync<bool>(
            database, "select to_regprocedure('" + CostFunction + "') is not null")).ShouldBeTrue("the cost overload exists");
        (await _fixture.ScalarAsync<string>(
            database, "select prosrc from pg_proc where oid = '" + Function + "'::regprocedure"))
            .ShouldNotContain("rate_limit_window", Case.Sensitive,
                "the wrapper body holds no table path of its own, so an inlined copy of the decision logic would be visible here");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select allowed from evidgate_resolve.rate_limit_acquire('test_0112', sha256('wrapper'::bytea), 5, 60, 100)"))
            .ShouldBeTrue();
        (await StoredHitsAsync(database, "wrapper")).ShouldBe(1L, "the five-argument wrapper spends one unit");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select allowed from evidgate_resolve.rate_limit_acquire("
            + "'test_0112', sha256('cost'::bytea), 5, 60, 100, 3)")).ShouldBeTrue();
        (await StoredHitsAsync(database, "cost")).ShouldBe(3L, "the six-argument form spends the cost it is given");
        (await TableAclAsync(database)).ShouldBe(tableAcl);
        (await PolicyCountAsync(database)).ShouldBe(1L);
        foreach (var role in RuntimeRoles)
        {
            (await HasTablePrivilegeAsync(database, role)).ShouldBeFalse($"{role} still has no direct table access");
        }

        (await PublicAclEntriesAsync(database, function: false)).ShouldBe(0L);
    }

    [Theory]
    [InlineData(Function)]
    [InlineData(CostFunction)]
    public async Task CostMigration_EachOverload_KeepsDefinerHygieneAndAppOnlyExecute(string signature)
    {
        var database = await AppliedAsync(CostFile);

        (await _fixture.ScalarAsync<bool>(
            database, "select prosecdef from pg_proc where oid = '" + signature + "'::regprocedure")).ShouldBeTrue();
        (await _fixture.ScalarAsync<string>(
            database, "select pg_get_userbyid(proowner) from pg_proc where oid = '" + signature + "'::regprocedure"))
            .ShouldBe("evidgate_owner");
        (await _fixture.ScalarAsync<bool>(
            database, "select coalesce('search_path=\"\"' = any(proconfig), false) from pg_proc "
                      + "where oid = '" + signature + "'::regprocedure")).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_proc p "
            + "cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a "
            + "where p.oid = '" + signature + "'::regprocedure and a.grantee = 0 and a.privilege_type = 'EXECUTE'"))
            .ShouldBe(0L);
        (await HasExecuteAsync(database, "evidgate_app", signature)).ShouldBeTrue("the app is the positive control");
        (await HasExecuteAsync(database, "evidgate_owner", signature)).ShouldBeTrue();
        foreach (var role in RuntimeRoles[1..])
        {
            (await HasExecuteAsync(database, role, signature)).ShouldBeFalse($"{role} must not execute {signature}");
        }
    }

    [Fact]
    public async Task CostMigration_ReplayedOutsideJournal_PreservesBothOverloadsAndTablePrivileges()
    {
        var database = await AppliedAsync(CostFile);
        var tableAcl = await TableAclAsync(database);
        var again = RunHead(database, CostFile);
        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBeEmpty("the journal skips the applied file");

        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var replay = connection.CreateCommand();
        replay.CommandText = await File.ReadAllTextAsync(
            Path.Combine(PostgresMigratorFixture.RepoMigrationsPath, CostFile),
            TestContext.Current.CancellationToken);
        await replay.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        (await AcquireFunctionCountAsync(database)).ShouldBe(2L);
        (await TableAclAsync(database)).ShouldBe(tableAcl);
        (await PolicyCountAsync(database)).ShouldBe(1L);
        (await PublicAclEntriesAsync(database, function: true)).ShouldBe(0L);
        foreach (var signature in new[] { Function, CostFunction })
        {
            (await HasExecuteAsync(database, "evidgate_app", signature)).ShouldBeTrue();
        }
    }

    /// <summary>A fresh database brought to the head just before this file.</summary>
    private async Task<string> PreviousHeadAsync(string rateFile = RateFile)
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create();
        migrations.CopyReal(
            [.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, rateFile) < 0)]);

        var outcome = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, outcome.Transcript);

        return database;
    }

    /// <summary>The previous head plus this file, with the file's own application asserted.</summary>
    private async Task<string> AppliedAsync(string rateFile = RateFile)
    {
        var database = await PreviousHeadAsync(rateFile);
        var applied = RunHead(database, rateFile);

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([rateFile]);

        return database;
    }

    /// <summary>Runs the real files up to this migration, so the journal decides what is applied.</summary>
    private RunOutcome RunHead(string database, string rateFile = RateFile)
    {
        using var migrations = TempMigrations.Create();
        migrations.CopyReal(
            [.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, rateFile) <= 0)]);

        return MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
    }

    private Task<string> TableAclAsync(string database) =>
        _fixture.ScalarAsync<string>(
            database,
            "select coalesce(relacl::text, '') from pg_class where oid = 'public.rate_limit_window'::regclass");

    private Task<long> StoredHitsAsync(string database, string keySeed) =>
        _fixture.ScalarAsync<long>(
            database,
            "select hit_count::bigint from public.rate_limit_window "
            + "where surface = 'test_0112' and key_hash = sha256('" + keySeed + "'::bytea)");

    private Task<long> AcquireFunctionCountAsync(string database) =>
        _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace "
            + "where n.nspname = 'evidgate_resolve' and p.proname = 'rate_limit_acquire'");

    /// <summary>Row-level policies on the window table; safe before the table exists (no <c>regclass</c> cast).</summary>
    private Task<long> PolicyCountAsync(string database) =>
        _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_policy p join pg_class c on c.oid = p.polrelid "
            + "where c.relnamespace = 'public'::regnamespace and c.relname = 'rate_limit_window'");

    private Task<bool> HasTablePrivilegeAsync(string database, string role) =>
        _fixture.ScalarAsync<bool>(
            database,
            "select has_table_privilege('" + role + "', '" + Table + "', "
            + "'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')");

    private Task<bool> HasExecuteAsync(string database, string role, string signature = Function) =>
        _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('" + role + "', '" + signature + "', 'EXECUTE')");

    /// <summary>ACL entries granted to PUBLIC (grantee 0) on the table or the definer; none are allowed.</summary>
    private Task<long> PublicAclEntriesAsync(string database, bool function) =>
        _fixture.ScalarAsync<long>(
            database,
            function
                ? "select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace "
                  + "cross join lateral aclexplode(coalesce(p.proacl, '{}'::aclitem[])) a "
                  + "where n.nspname = 'evidgate_resolve' and p.proname = 'rate_limit_acquire' and a.grantee = 0"
                : "select count(*) from pg_class c "
                  + "cross join lateral aclexplode(coalesce(c.relacl, '{}'::aclitem[])) a "
                  + "where c.oid = 'public.rate_limit_window'::regclass and a.grantee = 0");
}
