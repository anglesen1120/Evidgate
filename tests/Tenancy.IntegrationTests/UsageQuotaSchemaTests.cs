using System.IO;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The quota counter against a real PostgreSQL: the tenant keys, the coalesce scope index the gate's single
/// conditional UPDATE reads through, the closed vocabulary and numeric bounds the gate's arithmetic relies on,
/// the app role's column-scoped write surface (never a scope or tenant column, never DELETE), the composite
/// project reference and FORCE row level security with the app-scoped tenant policy.
/// </summary>
/// <remarks>
/// The gate statement itself is explained from its production text (<see cref="UsageQuotaGateSql"/>): the
/// assertion follows the served SQL instead of a copy, and the seed is what makes the planner prefer the
/// index, never <c>enable_seqscan = off</c>.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class UsageQuotaSchemaTests
{
    private const string Table = "quota_counter";
    private const string MigrationFile = "0078_usage_quota_counter.sql";

    /// <summary>The sentinel the scope index substitutes for a null <c>project_id</c>.</summary>
    private const string NullProjectSentinel = "00000000-0000-0000-0000-000000000000";

    private readonly PostgresTenancyFixture _fixture;

    public UsageQuotaSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) the migration and the declared shape

    [Fact]
    public async Task Migration_IsJournalled_AndDeclaresTheCounterContract()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue("the counter is created by its migration");

        var journal = await _fixture.ReadJournalAsync(database);
        journal.ShouldContain(row => row.Name == MigrationFile, $"{MigrationFile} must be journalled by the real runner");
        journal.ShouldAllBe(row => row.Checksum.Length == 64);

        var columns = await _fixture.ColumnsAsync(database, Table);
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:NO:",
            "project_id:uuid:uuid:YES:",
            "period:text:text:NO:",
            "metric:text:text:NO:",
            "used:numeric:numeric:NO:0",
            "quota_limit:numeric:numeric:NO:",
            "warn_ratio:numeric:numeric:NO:0.8",
            "warned_at:timestamp with time zone:timestamptz:YES:",
            "blocked_at:timestamp with time zone:timestamptz:YES:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:1",
        })
        {
            columns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"quota_counter needs the contracted column shape '{prefix}'");
        }

        columns.Count.ShouldBe(13, "the counter holds its scope, its numbers, the two threshold stamps and the audit triple");

        var constraints = await _fixture.ConstraintNamesAsync(database, Table);
        constraints.ShouldContain("p:quota_counter_pkey");
        constraints.ShouldContain("u:quota_counter_org_id_key", "the composite-FK target every tenant table declares");
        constraints.ShouldContain("f:quota_counter_project_fk");
        foreach (var check in new[]
        {
            "period", "metric", "used", "quota_limit", "warn_ratio", "version",
        })
        {
            constraints.ShouldContain($"c:quota_counter_{check}_check", $"quota_counter needs quota_counter_{check}_check");
        }

        // The reference is tenant-qualified: a counter can only name its own org's project.
        var projectKey = (await _fixture.ConstraintsAsync(database, Table))
            .Single(constraint => constraint.Contains("quota_counter_project_fk", StringComparison.Ordinal));
        projectKey.ShouldContain("(org_id, project_id)");
        projectKey.ShouldContain("project(org_id, id)");

        var indexes = await _fixture.IndexesAsync(database, Table);
        indexes.ShouldContain(
            index => index.StartsWith("quota_counter_scope_key:", StringComparison.Ordinal)
                && index.Contains("UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
                && index.Contains("coalesce(project_id", StringComparison.OrdinalIgnoreCase),
            "the gate reads through the unique index over coalesce(project_id, sentinel)");
        indexes.ShouldContain(
            index => index.StartsWith("quota_counter_project_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id)", StringComparison.Ordinal),
            "quota_counter_project_idx must serve the composite project FK (0028 precedent)");
    }

    [Fact]
    public async Task MigrationSql_RunsAgainAsANoOp()
    {
        // Dedicated: a re-run touches every policy, trigger and grant of the table.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        var path = Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile);
        var sql = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        // The template already holds the migration, so a second run must change nothing: that is what the
        // guards, the `if not exists` clauses and the drop-then-create order are for. It runs as the owner
        // role, the role the migrator uses, so ownership and grants stay put.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await owner.ExecuteAsync(sql);
            await owner.CommitAsync();
        }

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from information_schema.columns where table_schema = 'public' and table_name = '{Table}'"))
            .ShouldBe(13, "a re-run adds no column");
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_constraint where conrelid = 'public.quota_counter'::regclass "
            + "and conname = 'quota_counter_project_fk'"))
            .ShouldBe(1, "the project key is not added twice");
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_trigger where tgrelid = 'public.quota_counter'::regclass "
            + "and tgname = 'quota_counter_set_updated_at_and_version'"))
            .ShouldBe(1);
        (await _fixture.PoliciesAsync(database, Table)).Count.ShouldBe(2, "the two policies are replaced, never stacked");
    }

    [Fact]
    public async Task QuotaCounter_HasForcedRls_AndTheAppScopedTenantPolicy()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var rls = await _fixture.RowSecurityAsync(database, Table);
        rls.Enabled.ShouldBeTrue("DECISIONS F5 requires ENABLE row level security");
        rls.Forced.ShouldBeTrue("DECISIONS F5 requires FORCE row level security");

        // A policy with no `to` clause applies to PUBLIC, so both halves are named: the tenant policy for the
        // app role and the permissive owner policy FORCE requires, with no operator policy at all.
        (await _fixture.PoliciesAsync(database, Table)).ShouldBe(
        [
            "quota_counter_app_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            "quota_counter_owner_backfill:evidgate_owner:*:true:true",
        ]);
    }

    // ------------------------------------------------------------------ (b) the scope key

    [Fact]
    public async Task QuotaCounter_ScopeKey_CountsANullProjectAsOneScope()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (otherOrg, _) = await SeedTenantAsync(database, "beta", "store");

        var tenantLevelId = Guid.CreateVersion7();
        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(tenantLevelId, org, projectId: null, used: 10m));

        // Two tenant-level counters for one period and metric are the same scope: a plain four-column unique
        // would accept both, which is why the index coalesces the project. A refused statement aborts its
        // transaction, so each attempt gets its own scope.
        var duplicate = QuotaInsert(Guid.CreateVersion7(), org, projectId: null, used: 12m);
        var nullProjectError = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, duplicate.Sql, duplicate.Parameters);
        nullProjectError.SqlState.ShouldBe("23505");
        nullProjectError.ConstraintName.ShouldBe("quota_counter_scope_key");

        // A second counter may not reuse the row id either.
        var reusedId = QuotaInsert(tenantLevelId, org, projectId: null, period: "2026-12");
        var reusedIdError = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, reusedId.Sql, reusedId.Parameters);
        reusedIdError.SqlState.ShouldBe("23505");
        reusedIdError.ConstraintName.ShouldBeOneOf(["quota_counter_pkey", "quota_counter_org_id_key"], "a reused row id breaks a tenant key");

        // The same period and metric for a real project is a different scope, and so is another metric or period.
        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(Guid.CreateVersion7(), org, project));
        var scopedDuplicate = QuotaInsert(Guid.CreateVersion7(), org, project, used: 4m);
        var scopedError = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, scopedDuplicate.Sql, scopedDuplicate.Parameters);
        scopedError.SqlState.ShouldBe("23505");
        scopedError.ConstraintName.ShouldBe("quota_counter_scope_key");

        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(Guid.CreateVersion7(), org, project, metric: "ai_tokens"));
        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(Guid.CreateVersion7(), org, project, period: "2026-11"));

        // The key is per tenant: another org's identical scope is its own counter.
        await ExecuteAsync(_fixture.AppConnectionString(database), otherOrg, QuotaInsert(Guid.CreateVersion7(), otherOrg, projectId: null));

        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.quota_counter where org_id = '{org}'")).ShouldBe(4);
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.quota_counter where org_id = '{otherOrg}'")).ShouldBe(1);
    }

    [Fact]
    public async Task QuotaCounter_RaiseUpsert_ReusesTheScopeRow()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var first = QuotaInsert(Guid.CreateVersion7(), org, projectId: null, used: 5m, quotaLimit: 100m);
        (await app.ExecuteAsync(first.Sql, first.Parameters)).ShouldBe(1);

        // The raise's own statement shape: the arbiter is the expression the unique index is built on, so a
        // scope that already has a counter takes the new limit instead of gaining a second row.
        var raise = QuotaInsert(Guid.CreateVersion7(), org, projectId: null, quotaLimit: 250m);
        var upserted = await app.ExecuteAsync(
            raise.Sql
            + $" on conflict (org_id, coalesce(project_id, '{NullProjectSentinel}'::uuid), period, metric) "
            + "do update set quota_limit = excluded.quota_limit, blocked_at = null",
            raise.Parameters);

        upserted.ShouldBe(1, "the raise must hit the scope index instead of inserting a second counter");

        (await app.ScalarAsync<long>($"select count(*) from public.quota_counter where org_id = '{org}'")).ShouldBe(1);
        (await app.ScalarAsync<long>(
            $"select count(*) from public.quota_counter where org_id = '{org}' "
            + "and project_id is null and period = '2026-10' and metric = 'results' "
            + "and used = 5 and quota_limit = 250 and version = 2"))
            .ShouldBe(1, "an upserted counter keeps the consumed quantity, takes the new limit and bumps version");
    }

    // ------------------------------------------------------------------ (c) the reference and the tenant boundary

    [Fact]
    public async Task QuotaCounter_ForeignOrgProject_IsRefused()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (_, foreignProject) = await SeedTenantAsync(database, "beta", "store");

        // The positive control first: the org's own project is a valid scope.
        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(Guid.CreateVersion7(), org, project));

        // The policy's with-check passes (the row names its own org), so the composite key is what refuses it.
        var foreign = QuotaInsert(Guid.CreateVersion7(), org, foreignProject);
        var error = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, foreign.Sql, foreign.Parameters);

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("quota_counter_project_fk");
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.quota_counter where org_id = '{org}'")).ShouldBe(1);
    }

    [Fact]
    public async Task QuotaCounter_CrossTenantRead_ZeroRows()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (otherOrg, _) = await SeedTenantAsync(database, "beta", "store");

        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(Guid.CreateVersion7(), org, project, used: 7m));

        // Another tenant's scope sees no counter, and the payload's own org decides the with-check.
        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), otherOrg))
        {
            (await other.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(otherOrg);
            (await other.ScalarAsync<long>("select count(*) from public.quota_counter")).ShouldBe(0);

            var foreign = QuotaInsert(Guid.CreateVersion7(), org, project);
            var denied = await other.CaptureErrorAsync(foreign.Sql, foreign.Parameters);
            denied.SqlState.ShouldBe("42501", "a cross-tenant counter INSERT must fail the with-check");
        }

        // No tenant context: the helper returns NULL, so the policy matches no row.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await unset.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await unset.ScalarAsync<long>("select count(*) from public.quota_counter")).ShouldBe(0);
        }

        // The operator holds nothing on the counter, so even its SELECT is refused rather than answered.
        await using (var op = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database), org))
        {
            var error = await op.CaptureErrorAsync("select count(*) from public.quota_counter");
            error.SqlState.ShouldBe("42501");
        }

        // The counter is still there for its own tenant, so the zeros above are the policy.
        await using (var own = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await own.ScalarAsync<long>("select count(*) from public.quota_counter")).ShouldBe(1);
        }
    }

    // ------------------------------------------------------------------ (d) the bounds the gate's arithmetic assumes

    [Fact]
    public async Task QuotaCounter_CheckBoundaries_RejectBadPeriodMetricAndNumbers()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var app = _fixture.AppConnectionString(database);

        // The positive controls: both lower bounds are inclusive and the warning line is a strict interior.
        await ExecuteAsync(app, org, QuotaInsert(Guid.CreateVersion7(), org, project, used: 0m, quotaLimit: 0m, period: "2025-01", warnRatio: 0.0001m));
        await ExecuteAsync(app, org, QuotaInsert(Guid.CreateVersion7(), org, project, used: 0m, quotaLimit: 0m, period: "2025-02", warnRatio: 0.9999m));

        // PostgreSQL stores NaN and Infinity in a numeric column and orders NaN above every number, so only an
        // explicit CHECK refuses them; the connection cannot carry those values as a parameter that reads.
        string RawInsert(string period, string used, string quotaLimit) =>
            $"insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit, warn_ratio) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', '{project}', '{period}', 'results', {used}, {quotaLimit}, 0.8)";

        var noParameters = Array.Empty<(string Name, object Value)>();

        var cases = new List<(string Label, (string Sql, (string Name, object Value)[] Parameters) Insert, string Constraint)>
        {
            ("a single-digit month", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-3"), "quota_counter_period_check"),
            ("a thirteenth month", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-13"), "quota_counter_period_check"),
            ("a period with a suffix", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-01-01"), "quota_counter_period_check"),
            ("a period before the counter's year range", QuotaInsert(Guid.CreateVersion7(), org, project, period: "1999-12"), "quota_counter_period_check"),
            ("a period after the counter's year range", QuotaInsert(Guid.CreateVersion7(), org, project, period: "3000-01"), "quota_counter_period_check"),
            ("an undeclared metric", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-04", metric: "requests"), "quota_counter_metric_check"),
            ("a negative consumed quantity", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-05", used: -1m), "quota_counter_used_check"),
            ("a NaN consumed quantity", (RawInsert("2025-11", "'NaN'::numeric", "100"), noParameters), "quota_counter_used_check"),
            ("an infinite consumed quantity", (RawInsert("2025-12", "'Infinity'::numeric", "100"), noParameters), "quota_counter_used_check"),
            ("a negative limit", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-06", quotaLimit: -1m), "quota_counter_quota_limit_check"),
            ("a NaN limit", (RawInsert("2026-01", "0", "'NaN'::numeric"), noParameters), "quota_counter_quota_limit_check"),
            ("an infinite limit", (RawInsert("2026-02", "0", "'Infinity'::numeric"), noParameters), "quota_counter_quota_limit_check"),
            ("a zero warning line", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-07", warnRatio: 0m), "quota_counter_warn_ratio_check"),
            ("a warning line at the limit", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-08", warnRatio: 1m), "quota_counter_warn_ratio_check"),
            ("a warning line above the limit", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-09", warnRatio: 1.2m), "quota_counter_warn_ratio_check"),
            ("a zero concurrency token", QuotaInsert(Guid.CreateVersion7(), org, project, period: "2025-10", version: 0), "quota_counter_version_check"),
        };

        foreach (var (label, insert, constraint) in cases)
        {
            var error = await CaptureErrorAsync(app, org, insert.Sql, insert.Parameters);

            error.SqlState.ShouldBe("23514", $"{label} must violate the counter CHECK");
            error.ConstraintName.ShouldBe(constraint, $"{label} must be refused by {constraint}");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.quota_counter where org_id = '{org}'"))
            .ShouldBe(2, "only the two in-bound rows exist; every refused row wrote nothing");
    }

    [Fact]
    public async Task QuotaCounter_Increment_BumpsTheVersionToken()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var id = Guid.CreateVersion7();

        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(id, org, project, used: 0m));

        // One increment in its own transaction: the shared trigger owns updated_at and version, so the token
        // the row is read-modified-written under moves on every metered batch.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await app.ExecuteAsync($"update public.quota_counter set used = 3 where id = '{id}'")).ShouldBe(1);
            (await app.ScalarAsync<int>($"select version from public.quota_counter where id = '{id}'")).ShouldBe(2);
            (await app.ScalarAsync<bool>(
                $"select updated_at >= created_at from public.quota_counter where id = '{id}'")).ShouldBeTrue();
            await app.CommitAsync();
        }
    }

    // ------------------------------------------------------------------ helpers

    private const string QuotaColumns = "(id, org_id, period, metric, used, quota_limit, warn_ratio, version";

    /// <summary>
    /// One counter INSERT. The project column is appended only when the scope has one, so a tenant-level limit
    /// never needs a typed NULL parameter (00 §5.7 S1: values stay parameterised).
    /// </summary>
    private static (string Sql, (string Name, object Value)[] Parameters) QuotaInsert(
        Guid id,
        Guid org,
        Guid? projectId,
        decimal used = 0m,
        decimal quotaLimit = 100m,
        string period = "2026-10",
        string metric = "results",
        decimal warnRatio = 0.8m,
        int version = 1)
    {
        var columns = QuotaColumns + (projectId is null ? string.Empty : ", project_id");
        var values = "values (@id, @org_id, @period, @metric, @used, @quota_limit, @warn_ratio, @version"
            + (projectId is null ? string.Empty : ", @project_id");
        var parameters = new List<(string Name, object Value)>
        {
            ("id", id),
            ("org_id", org),
            ("period", period),
            ("metric", metric),
            ("used", used),
            ("quota_limit", quotaLimit),
            ("version", version),
            ("warn_ratio", warnRatio),
        };

        if (projectId is { } project)
        {
            parameters.Add(("project_id", project));
        }

        return ($"insert into public.quota_counter {columns}) {values})", parameters.ToArray());
    }

    /// <summary>An org and a project in it, written as the migration role.</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", orgSlug),
            ("slug", Unique.Slug(orgSlug)));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", projectSlug),
            ("slug", projectSlug));
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>Runs one INSERT as the app role in a fresh tenant transaction and commits exactly one row.</summary>
    private static async Task ExecuteAsync(
        string connectionString,
        Guid org,
        (string Sql, (string Name, object Value)[] Parameters) insert)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Runs one statement in a fresh transaction and returns the server error it raised.</summary>
    private static async Task<PostgresException> CaptureErrorAsync(
        string connectionString,
        Guid org,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        return await scope.CaptureErrorAsync(sql, parameters);
    }
}
