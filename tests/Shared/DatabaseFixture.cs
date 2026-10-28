using System.Diagnostics;
using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Readiness;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

// 01.6.1-T2.1: the harness registers itself as an xUnit assembly fixture at file
// scope (the same trick as the no-network guard of 01.6.1-T1.1), so a project that
// links this file gets one container + one migrated database per assembly without
// wiring anything in its csproj or its test classes — the test class only has to
// ask for a DatabaseFixture in its constructor.
[assembly: AssemblyFixture(typeof(Evidgate.Testing.DatabaseFixture))]

namespace Evidgate.Testing;

/// <summary>
/// The shared integration harness of 01.6.1-T2 (00 §5.4, §5.7 P-): one
/// digest-pinned PostgreSQL 18 container per test assembly, one database created
/// and brought to head once by the real <c>Evidgate.Migrator</c> as
/// <c>evidgate_owner</c>, then handed to the tests as <c>evidgate_app</c> — the
/// least-privilege role of 00 §5.4.3, so every test runs under RLS instead of
/// around it (AC1, AC3).
/// <para>
/// The database is shared, so <see cref="ResetAsync"/> is what isolates one test
/// from the next (01.6.1-T2.2, AC2) and the harness collection is serialised — see
/// <see cref="HarnessSuite"/> and <c>tests/README.md</c>.
/// </para>
/// <para>
/// It differs from the sibling fixtures that read <see cref="ContainerImages"/>
/// (<c>tests/Persistence.IntegrationTests</c>, <c>tests/Tasks.IntegrationTests</c>)
/// in one way only: those create a fresh database per test — no reset risk, but a
/// full migration run per test — while this harness migrates once per assembly and
/// resets with Respawn between tests, which is what makes a shared suite
/// affordable.
/// </para>
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    /// <summary>
    /// Wall-clock budget of one harness assembly run (01.6.1 §8, AC3: < 10 min
    /// locally). Measured around the whole assembly — container start, Migrator and
    /// every reset — printed at the end of the run, and enforced by
    /// <see cref="DisposeAsync"/> so the budget is a gate rather than a note.
    /// </summary>
    public static readonly TimeSpan SuiteBudget = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound of the lazy start (image pull, boot, migrations).</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private const string DatabaseOwnerRole = "evidgate_owner";
    private const string ApplicationRole = "evidgate_app";
    private const string HarnessDatabase = "evidgate_harness";

    // Built inside the lazy start, not here: building a container resolves the Docker
    // endpoint and throws when there is no daemon, which would fail the assembly
    // fixture's construction for tests that never need a container.
    private PostgreSqlContainer? _container;

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly Stopwatch _suiteClock = new();

    private NpgsqlDataSource? _appDataSource;
    private ServiceProvider? _services;
    private RespawnCheckpoint? _checkpoint;
    private readonly object _startLock = new();
    private Task? _start;
    private int _resets;

    /// <summary>
    /// Repository <c>migrations/</c>, resolved by the Migrator's own default-path
    /// rule (walk up from the test directory to <c>Evidgate.slnx</c>), so the
    /// harness applies the real files through the real entry point.
    /// </summary>
    public static string MigrationsPath { get; } = MigratorOptions.FromEnvironment(
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = "Host=127.0.0.1;Database=evidgate;Username=evidgate_owner",
        }).MigrationsPath;

    /// <summary>
    /// The migration journal head this build expects (baked into
    /// <c>Evidgate.BuildingBlocks.Persistence</c> at build time, 01.1.2-T1.3), i.e.
    /// the lexically greatest <c>migrations/*.sql</c> name.
    /// </summary>
    public static string ExpectedHead => ExpectedMigrationHead.Value;

    /// <summary>The app-role connection string every test body must use (AC3).</summary>
    public string AppConnectionString
    {
        get
        {
            EnsureStarted();
            return Build(ApplicationRole, _appPassword, HarnessDatabase);
        }
    }

    /// <summary>The migration-owner connection string, for the seeding seam only (00 §5.5 C3).</summary>
    public string OwnerConnectionString
    {
        get
        {
            EnsureStarted();
            return Build(DatabaseOwnerRole, _ownerPassword, HarnessDatabase);
        }
    }

    /// <summary>The app-role data source the tenant scope factory borrows its connections from.</summary>
    public NpgsqlDataSource AppDataSource
    {
        get
        {
            EnsureStarted();
            return _appDataSource!;
        }
    }

    /// <summary>The app-role scope factory of the production registration (01.4.2).</summary>
    public ITenantScopeFactory Scopes =>
        StartedServices().GetRequiredService<ITenantScopeFactory>();

    /// <summary>The Dapper read path of the production registration: throws without an open scope (C3).</summary>
    public ITenantDb Db =>
        StartedServices().GetRequiredService<ITenantDb>();

    /// <summary>
    /// The EF Core command guard of the production registration (01.4.2-T1.5): a
    /// context that carries it refuses every command that does not run on the
    /// scope's connection. Exposed so a test can build a probe context without a
    /// module context (there is none in this project).
    /// </summary>
    public TenantCommandInterceptor Interceptor =>
        StartedServices().GetRequiredService<TenantCommandInterceptor>();

    /// <summary>How many <see cref="ResetAsync"/> calls this assembly has made.</summary>
    public int Resets => _resets;

    /// <summary>
    /// Starts the suite clock only. The container is booted lazily by the first
    /// accessor or method that needs the database (<see cref="EnsureStarted"/>), so an
    /// assembly whose tests never touch it — for example the file-loading tests of
    /// <c>Foundation.IntegrationTests</c> — never starts a container and runs where
    /// no Docker daemon exists (the clean-build smoke, <c>scripts/smoke-clean-build.sh</c>).
    /// </summary>
    public ValueTask InitializeAsync()
    {
        // The clock covers container start, migrations and every test of the
        // assembly: the budget of 01.6.1 §8 is a suite budget, not a per-test one.
        _suiteClock.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Single-flight start: the first caller boots the harness, every other caller
    /// (including concurrent ones) awaits the same task, and a failed start is
    /// rethrown to each of them. The work runs on the thread pool and is not bound
    /// to the first test's cancellation token, so one test ending does not abort the
    /// shared start.
    /// </summary>
    private Task EnsureStartedAsync()
    {
        lock (_startLock)
        {
            return _start ??= Task.Run(StartAsync);
        }
    }

    /// <summary>
    /// Synchronous accessors (<see cref="AppConnectionString"/>, <see cref="Scopes"/>
    /// …) cannot await; the start runs on the pool, so blocking here cannot deadlock
    /// on the test's synchronization context.
    /// </summary>
#pragma warning disable VSTHRD002 // the start runs on the pool via Task.Run, see above
    private void EnsureStarted() => EnsureStartedAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    private ServiceProvider StartedServices()
    {
        EnsureStarted();
        return _services!;
    }

    /// <summary>
    /// Boots the container, creates the owner role exactly as the dev/bootstrap
    /// script does (LOGIN + CREATEROLE, never SUPERUSER/BYPASSRLS — DECISIONS F5),
    /// creates the harness database owned by that role, migrates it to head through
    /// the real <c>Evidgate.Migrator</c>, then enables the app login. Once per
    /// assembly, so the per-test cost is a Respawn reset, not a migration run.
    /// </summary>
    private async Task StartAsync()
    {
        _container = new PostgreSqlBuilder(ContainerImages.Postgres)
            .WithDatabase("evidgate")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        using var timeout = new CancellationTokenSource(StartTimeout);
        var cancellationToken = timeout.Token;
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        await using (var connection = await OpenSuperuserCoreAsync("evidgate", cancellationToken).ConfigureAwait(false))
        {
            // The container is fresh, so the owner role does not exist yet; migration
            // 0001 creates the three application roles, and the superuser is the only
            // role that may create/alter a LOGIN credential (01 §6).
            await ExecuteAsync(
                connection,
                $"create role {DatabaseOwnerRole} login createrole nosuperuser nocreatedb nobypassrls "
                + $"password '{_ownerPassword}'",
                cancellationToken);
            await ExecuteAsync(
                connection,
                $"create database \"{HarnessDatabase}\" owner {DatabaseOwnerRole}",
                cancellationToken);
        }

        // The database is owned by the migration role, so `public` (owned by
        // pg_database_owner since PostgreSQL 15) is writable by the migrations and by
        // nothing else — the bootstrap the persistence fixture uses.
        var outcome = MigratorRunner.Run(
            new MigratorOptions
            {
                ConnectionString = Build(DatabaseOwnerRole, _ownerPassword, HarnessDatabase),
                MigrationsPath = MigrationsPath,
            },
            MigrationCommand.Apply,
            new StringWriter(CultureInfo.InvariantCulture));

        if (outcome.ExitCode != MigratorExitCodes.Success)
        {
            throw new InvalidOperationException(
                $"the harness could not reach head on '{HarnessDatabase}' (exit {outcome.ExitCode}): "
                + string.Join(" | ", outcome.Messages));
        }

        await using (var connection = await OpenSuperuserCoreAsync(HarnessDatabase, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(
                connection,
                $"alter role {ApplicationRole} login password '{_appPassword}'",
                cancellationToken);
        }

        _appDataSource = NpgsqlDataSource.Create(Build(ApplicationRole, _appPassword, HarnessDatabase));

        // The production registration under test (00 §5.5 C3, 01.4.2): the scope
        // factory and the Dapper path over the app-role data source, exactly as
        // Evidgate.Api/Evidgate.Worker wire them.
        var services = new ServiceCollection();
        services.AddSingleton(_appDataSource);
        services.AddEvidgateTenancy();
        _services = services.BuildServiceProvider();

        // Respawn is set up once (it reads the FK graph then); resets only replay the
        // delete plan.
        _checkpoint = await RespawnCheckpoint.CreateAsync(
            Build(DatabaseOwnerRole, _ownerPassword, HarnessDatabase),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the clock, prints the assembly wall time, and fails the run when the
    /// suite overran <see cref="SuiteBudget"/> (AC3). Disposal happens after every
    /// test, so the elapsed value is the suite wall time the NFR-01-1 budget speaks
    /// about.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _suiteClock.Stop();
        var elapsed = _suiteClock.Elapsed;

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"[evidgate harness] suite wall time {elapsed.TotalSeconds:F1}s of a {SuiteBudget.TotalMinutes:F0} min budget "
                + $"after {_resets} Respawn reset(s); database '{HarnessDatabase}'."));

        Task? start;

        lock (_startLock)
        {
            start = _start;
        }

        // Never started (no test needed the database): nothing to tear down. A
        // start that failed already surfaced to the tests that asked for it.
        if (start is not null)
        {
            try
            {
                await start.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already reported through the accessor that triggered it; disposal
                // below still releases whatever was created.
            }

            if (_services is not null)
            {
                await _services.DisposeAsync();
            }

            if (_appDataSource is not null)
            {
                await _appDataSource.DisposeAsync();
            }

            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }

        if (elapsed > SuiteBudget)
        {
            throw new InvalidOperationException(
                $"the harness suite took {elapsed.TotalMinutes:F1} min, over the {SuiteBudget.TotalMinutes:F0} min budget "
                + "(01.6.1-T2.4 AC3, NFR-01-1).");
        }
    }

    /// <summary>
    /// Empties every application table between two tests (01.6.1-T2.2, AC2) with one
    /// <c>truncate table … cascade</c>, and preserves <c>public.schema_migration</c>
    /// (<see cref="RespawnCheckpoint"/>) so head/readiness state survives a reset.
    /// <para>
    /// The reset runs as <c>evidgate_owner</c> because it owns the tables and
    /// TRUNCATE follows ownership: row level security is not involved (PostgreSQL
    /// never applies RLS to TRUNCATE) and neither is the owner's permissive policy.
    /// The app role cannot do it — 0001 gives it DML only, never TRUNCATE — and the
    /// container superuser is avoided on purpose.
    /// </para>
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        await _checkpoint!.ResetAsync(cancellationToken).ConfigureAwait(false);
        _resets++;
    }

    /// <summary>
    /// Opens a real tenant scope over the app-role connection for a seeded org
    /// (01.6.1-T2.3, AC4): <c>BeginAsync</c> borrows a connection from the harness's
    /// app data source, starts the transaction and applies
    /// <c>set_config('evidgate.org_id', …, true)</c>, so the returned scope is the
    /// same object the EF write model and the Dapper read path use.
    /// <para>
    /// This method is deliberately <b>not</b> <c>async</c>: the factory publishes the
    /// scope into its ambient <c>AsyncLocal</c> slot synchronously, in the caller's
    /// execution context. ExecutionContext flows only downwards, so a publication
    /// made inside an <c>async</c> method would never be visible to the test's own
    /// frame — and <see cref="ITenantDb"/> (like the EF interceptor) resolves the
    /// scope through that slot. Wrapping the call in <c>async</c> would hand out a
    /// scope that the Dapper path cannot see.
    /// </para>
    /// </summary>
    public Task<TenantTestScope> BeginTenantScopeAsync(
        Guid orgId,
        Guid? projectId = null,
        bool readOnlyTransaction = false,
        CancellationToken cancellationToken = default)
    {
        var db = Db;

        // Continued rather than awaited: this method is non-async on purpose (see
        // above), and an async wrapper would await the factory's task from a frame
        // the publication never reaches. The continuation runs synchronously on the
        // thread that completed the open and needs no ambient context of its own.
        return Scopes
            .BeginAsync(orgId, projectId, readOnlyTransaction, cancellationToken)
            .ContinueWith(
                opened => new TenantTestScope(opened.GetAwaiter().GetResult(), db),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Seeds an organization through the owner connection and returns its
    /// application-generated UUIDv7 (00 §7). Owner connection on purpose (00 §5.5
    /// C3): arranging rows is the one job a test may not do through the tenant path,
    /// because the app-role policy for <c>organization</c> requires the tenant
    /// setting to point at the very id being inserted.
    /// </summary>
    public Task<Guid> SeedOrgAsync(string? slug = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7();
        return SeedOrganizationAsync(id, slug, cancellationToken);
    }

    /// <summary>
    /// Seeds one organization (unless <paramref name="orgId"/> is given) plus a
    /// project under it, both as the owner, and returns the pair. The project's
    /// composite FK to <c>public.organization (id)</c> is what makes the pair the
    /// smallest complete tenant fixture.
    /// </summary>
    public async Task<(Guid OrgId, Guid ProjectId)> SeedProjectAsync(
        Guid? orgId = null,
        CancellationToken cancellationToken = default)
    {
        var org = orgId ?? Guid.CreateVersion7();

        if (orgId is null)
        {
            await SeedOrganizationAsync(org, null, cancellationToken).ConfigureAwait(false);
        }

        var project = Guid.CreateVersion7();
        await SeedAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            [
                ("id", project),
                ("org_id", org),
                ("name", "Seeded project"),
                ("slug", Slug("project")),
            ],
            cancellationToken).ConfigureAwait(false);

        return (org, project);
    }

    /// <summary>
    /// The one seeding seam of the harness: runs a statement as
    /// <c>evidgate_owner</c>. It exists for arranging rows only — an assertion that
    /// goes through the read/write path must use a tenant scope, never this
    /// connection (00 §5.5 C3).
    /// </summary>
    public async Task<int> SeedAsync(
        string sql,
        IReadOnlyList<(string Name, object Value)> values,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts <c>public.rls_probe</c> rows through the owner connection. Harness
    /// bookkeeping (proving Respawn emptied the schemas, AC2), not a tenant-path
    /// assertion: the owner holds the permissive policy of the 0003 copy block, so
    /// it sees every tenant's rows.
    /// </summary>
    public async Task<long> CountProbeRowsAsync(
        Guid? orgId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = orgId is null
            ? "select count(*) from public.rls_probe"
            : "select count(*) from public.rls_probe where org_id = @org_id";

        if (orgId is { } org)
        {
            command.Parameters.AddWithValue("org_id", org);
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The journal head as the app role reads it — the readiness path of
    /// 01.1.2-T1.3, which holds only <c>SELECT</c> on <c>public.schema_migration</c>
    /// (0001).
    /// </summary>
    public async Task<string?> JournalHeadAsync(CancellationToken cancellationToken = default) =>
        await AppScalarAsync<string>("select max(name) from public.schema_migration", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The number of journal rows, i.e. how many migrations the harness applied.</summary>
    public async Task<long> JournalCountAsync(CancellationToken cancellationToken = default) =>
        await AppScalarAsync<long>("select count(*) from public.schema_migration", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Every application table that still holds rows, read through a
    /// <b>superuser</b> connection. The census is deliberately not filtered by row
    /// level security: the point is "no row is left anywhere", and the owner would
    /// report zero rows for a table whose owner policy is missing while the rows are
    /// still there. Its job is to check the <em>outcome</em> of a reset rather than
    /// the mechanism: <see cref="RespawnCheckpoint"/> builds one truncate plan from
    /// the catalog when the harness starts, so a table that appears afterwards (or in
    /// a schema the plan missed) would keep a previous test's rows.
    /// <para>
    /// The table names come from <c>pg_class</c> and are re-emitted quoted by
    /// <c>format('%I')</c>, so the one place a statement is built by concatenation
    /// contains catalog output, never caller input.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> NonEmptyApplicationTablesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenSuperuserAsync(HarnessDatabase, cancellationToken);

        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select format('%I.%I', n.nspname, c.relname)
                  from pg_catalog.pg_class c
                  join pg_catalog.pg_namespace n on n.oid = c.relnamespace
                 where c.relkind = 'r'
                   and n.nspname <> 'information_schema'
                   and n.nspname not like 'pg\_%'
                   and c.relname <> 'schema_migration'
                 order by 1
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var nonEmpty = new List<string>();

        foreach (var table in tables)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"select count(*) from {table}";
            var rows = (long)(await count.ExecuteScalarAsync(cancellationToken))!;

            if (rows > 0)
            {
                nonEmpty.Add(string.Create(CultureInfo.InvariantCulture, $"{table} ({rows} row(s))"));
            }
        }

        return nonEmpty;
    }

    /// <summary>Opens a superuser connection (bootstrap only).</summary>
    public async Task<NpgsqlConnection> OpenSuperuserAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return await OpenSuperuserCoreAsync(database, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenSuperuserCoreAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(SuperuserConnectionString(database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<Guid> SeedOrganizationAsync(
        Guid id,
        string? slug,
        CancellationToken cancellationToken)
    {
        await SeedAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            [
                ("id", id),
                ("name", "Seeded organization"),
                ("slug", slug ?? Slug("org")),
            ],
            cancellationToken).ConfigureAwait(false);

        return id;
    }

    /// <summary>
    /// A slug matching the migration's grammar (<c>^[a-z0-9][a-z0-9-]{1,62}$</c>,
    /// 0004/0005): lowercase, starts alphanumeric, unique per generated suffix.
    /// </summary>
    private static string Slug(string prefix) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}");

    private async Task<T> AppScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var command = AppDataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string SuperuserConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database }.ConnectionString;

    private string Build(string username, string password, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = database,
            Username = username,
            Password = password,
        };

        return builder.ConnectionString;
    }
}

/// <summary>
/// The harness collection (01.6.1-T2.2): every Docker-backed test class of the
/// assembly joins it, and <c>DisableParallelization</c> keeps classes from
/// overlapping. That is not a preference but a consequence of the design — one
/// database per assembly reset by Respawn between tests means two classes running
/// at once would delete each other's rows. Parallelism lives across assemblies
/// instead (each <c>*.IntegrationTests</c> project boots its own container), and
/// the rules are written down in <c>tests/README.md</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HarnessSuite
{
    public const string Name = "postgres-harness";
}
