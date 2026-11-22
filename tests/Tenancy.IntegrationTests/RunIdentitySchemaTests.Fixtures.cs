using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class RunIdentitySchemaTests
{
    /// <summary>The migration under test, re-run by the idempotency case.</summary>
    private const string MigrationFile = "0037_run_identity.sql";

    /// <summary>40 lowercase hex chars: the SHA-1 shape of the GitHub <c>sha</c> claim (AC4).</summary>
    private const string FortyHex = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>A second 40-hex commit, so an UPDATE of <c>head_sha</c> is a real change.</summary>
    private const string OtherFortyHex = "fedcba9876543210fedcba9876543210fedcba98";

    /// <summary>64 lowercase hex chars: the SHA-256 repository shape AC4 also accepts.</summary>
    private const string SixtyFourHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>41 hex chars: one over the SHA-1 shape, so the regex must refuse it.</summary>
    private const string FortyOneHex = FortyHex + "a";

    /// <summary>63 hex chars: one under the SHA-256 shape, so the regex must refuse it.</summary>
    private const string SixtyThreeHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde";

    /// <summary>40 chars with an uppercase letter: GitHub's <c>sha</c> is lowercase only.</summary>
    private const string UppercaseHex = "0123456789ABCDEF0123456789ABCDEF01234567";

    /// <summary>40 chars with a non-hex letter.</summary>
    private const string NonHex = "0123456789abcdef0123456789abcdef0123456z";

    /// <summary>
    /// The valid baseline of the eight identity columns as raw SQL expressions: a branch run on
    /// <c>main</c> with a 40-hex head and a 64-hex base, no pull request, no fork, verified.
    /// Every rejection case overrides exactly one column of this row.
    /// </summary>
    private static readonly Dictionary<string, string> ValidIdentity = new(StringComparer.Ordinal)
    {
        ["ref_kind"] = "'branch'",
        ["ref_name"] = "'main'",
        ["pr_number"] = "null",
        ["head_sha"] = "'" + FortyHex + "'",
        ["base_sha"] = "'" + SixtyFourHex + "'",
        ["trigger_event"] = "'push'",
        ["is_fork"] = "false",
        ["unverified_ref"] = "false",
    };

    /// <summary>A full identity row: a fork pull-request run with both flags set (AC1/AC2).</summary>
    private static readonly (string Column, string Expression)[] FullIdentity =
    [
        ("ref_kind", "'pull_request'"),
        ("ref_name", "'refs/pull/42/merge'"),
        ("pr_number", "42"),
        ("trigger_event", "'pull_request'"),
        ("is_fork", "true"),
        ("unverified_ref", "true"),
    ];

    /// <summary>Every AC4 rejection: one identity column set to an out-of-bound value and the CHECK it must trip.</summary>
    public static TheoryData<string, string, string> BadIdentityShapes => new()
    {
        // The two closed vocabularies, plus the empty string.
        { "ref_kind", "'branch_name'", "run_ref_kind_check" },
        { "ref_kind", "''", "run_ref_kind_check" },
        { "trigger_event", "'workflow_call'", "run_trigger_event_check" },
        { "trigger_event", "''", "run_trigger_event_check" },

        // pr_number > 0: GitHub numbers pull requests from 1.
        { "pr_number", "0", "run_pr_number_check" },
        { "pr_number", "-7", "run_pr_number_check" },

        // ref_name 1..255: empty and one char over the bound.
        { "ref_name", "''", "run_ref_name_check" },
        { "ref_name", "repeat('a', 256)", "run_ref_name_check" },

        // head_sha: 41 and 63 hex, uppercase and non-hex are outside ^[0-9a-f]{40}([0-9a-f]{24})?$.
        { "head_sha", "'" + FortyOneHex + "'", "run_head_sha_check" },
        { "head_sha", "'" + SixtyThreeHex + "'", "run_head_sha_check" },
        { "head_sha", "'" + UppercaseHex + "'", "run_head_sha_check" },
        { "head_sha", "'" + NonHex + "'", "run_head_sha_check" },
        { "head_sha", "''", "run_head_sha_check" },

        // base_sha carries its own constraint, so it needs its own rows.
        { "base_sha", "'" + FortyOneHex + "'", "run_base_sha_check" },
        { "base_sha", "'" + UppercaseHex + "'", "run_base_sha_check" },
    };

    /// <summary>The three <c>ref_kind</c> values, with the number a pull-request run must carry (AC4).</summary>
    public static TheoryData<string, string> RefKinds => new()
    {
        { "'branch'", "null" },
        { "'tag'", "null" },
        { "'pull_request'", "42" },
    };

    /// <summary>The five <c>trigger_event</c> values.</summary>
    public static TheoryData<string> TriggerEvents => new()
    {
        "'push'", "'pull_request'", "'schedule'", "'manual'", "'dispatch'",
    };

    /// <summary>Both accepted sha shapes, on both columns.</summary>
    public static TheoryData<string, string> AcceptedShaShapes => new()
    {
        { "head_sha", "'" + FortyHex + "'" },
        { "head_sha", "'" + SixtyFourHex + "'" },
        { "base_sha", "'" + FortyHex + "'" },
        { "base_sha", "'" + SixtyFourHex + "'" },
    };

    /// <summary>Every identity column with a different, itself valid value: AC5 must refuse each one.</summary>
    public static TheoryData<string, string> ChangedIdentityValues => new()
    {
        { "ref_kind", "'tag'" },
        { "ref_name", "'release/v2'" },
        { "pr_number", "7" },
        { "head_sha", "'" + OtherFortyHex + "'" },
        { "base_sha", "'" + OtherFortyHex + "'" },
        { "trigger_event", "'schedule'" },
        { "is_fork", "false" },
        { "unverified_ref", "false" },
    };

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        DedicatedDatabase? Owned = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => WorldDisposal.DisposeAsync(Client, Owned, Api);
    }

    /// <summary>
    /// The parent rows a <c>run</c> row needs: the manifest with its suite and environment revisions.
    /// No test case or test revision is seeded — this story's table is <c>run</c> alone, and
    /// <c>planned_execution</c> (the only consumer of a test revision) is not written here.
    /// </summary>
    private sealed record Parents(Guid SuiteRevisionId, Guid EnvironmentRevisionId, Guid ManifestId);

    /// <summary>An org and a project on the shared database, created through the real registry routes.</summary>
    /// <param name="dedicated">A private database for the cases that need one; the world drops it.</param>
    private async Task<World> StartAsync(DedicatedDatabase? dedicated = null)
    {
        var database = dedicated?.Name ?? await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project, dedicated);
    }

    /// <summary>
    /// The manifest/suite/environment parents of one <c>run</c>, written as the migration role so a test
    /// can insert the row under test as the app role.
    /// </summary>
    private async Task<Parents> SeedParentsAsync(string database, Guid org, Guid project)
    {
        var suiteId = Guid.CreateVersion7();
        var suiteRevisionId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var environmentRevisionId = Guid.CreateVersion7();
        var manifestId = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        await scope.ExecuteAsync(
            "insert into public.suite (id, org_id, project_id, name, slug) values (@id, @org_id, @project_id, 'probe', 'probe-suite')",
            ("id", suiteId), ("org_id", org), ("project_id", project));
        await scope.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org_id, @suite_id, 1, @hash, '{"kind":"none"}', '{"max_retries":0}')
            """,
            ("id", suiteRevisionId), ("org_id", org), ("suite_id", suiteId), ("hash", "sha256:" + new string('a', 64)));
        await scope.ExecuteAsync(
            "insert into public.environment (id, org_id, project_id, environment_key, name) values (@id, @org_id, @project_id, 'staging', 'Staging')",
            ("id", environmentId), ("org_id", org), ("project_id", project));
        await scope.ExecuteAsync(
            """
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values (@id, @org_id, @project_id, @environment_id, 1, '{}', '{}', @hash, 'probe')
            """,
            ("id", environmentRevisionId), ("org_id", org), ("project_id", project), ("environment_id", environmentId),
            ("hash", "sha256:" + new string('c', 64)));
        await scope.ExecuteAsync(
            """
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values (@id, @org_id, @project_id, @hash, 1, @suite_revision_id, @environment_revision_id, '{}', '{}', '{}', 0, '{}', 'probe')
            """,
            ("id", manifestId), ("org_id", org), ("project_id", project), ("hash", "sha256:" + new string('d', 64)),
            ("suite_revision_id", suiteRevisionId), ("environment_revision_id", environmentRevisionId));

        await scope.CommitAsync();

        return new Parents(suiteRevisionId, environmentRevisionId, manifestId);
    }

    /// <summary>
    /// One raw insert of a run row as the app role with the valid identity baseline; every identity
    /// column is overridable with a raw SQL expression, so a rejection case can pass an invalid one.
    /// </summary>
    private static string InsertRunSql(
        Guid org,
        Guid project,
        Parents parents,
        Guid id,
        params (string Column, string Expression)[] identityOverrides)
    {
        var identity = new Dictionary<string, string>(ValidIdentity, StringComparer.Ordinal);

        foreach (var (column, expression) in identityOverrides)
        {
            identity.Keys.ShouldContain(column);
            identity[column] = expression;
        }

        var refKind = identity["ref_kind"];
        var refName = identity["ref_name"];
        var prNumber = identity["pr_number"];
        var headSha = identity["head_sha"];
        var baseSha = identity["base_sha"];
        var triggerEvent = identity["trigger_event"];
        var isFork = identity["is_fork"];
        var unverifiedRef = identity["unverified_ref"];

        return "insert into public.run "
            + "(id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes, "
            + "ref_kind, ref_name, pr_number, head_sha, base_sha, trigger_event, is_fork, unverified_ref) "
            + $"values ('{id}', '{org}', '{project}', '{parents.ManifestId}', '{parents.SuiteRevisionId}', '{parents.EnvironmentRevisionId}', "
            + "now() + interval '2 hours', 1440, "
            + $"{refKind}, {refName}, {prNumber}, {headSha}, {baseSha}, {triggerEvent}, {isFork}, {unverifiedRef})";
    }

    /// <summary>One committed run row for <paramref name="world"/>'s org, written as the app role.</summary>
    private async Task<Guid> InsertRunAsync(
        World world,
        Parents parents,
        Guid id,
        params (string Column, string Expression)[] identityOverrides)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertRunSql(world.Org, world.Project, parents, id, identityOverrides))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>One insert of a run row as the app role, expecting the server's rejection.</summary>
    private async Task<PostgresException> InsertRunExpectingErrorAsync(
        World world,
        Parents parents,
        params (string Column, string Expression)[] identityOverrides)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        return await scope.CaptureErrorAsync(
            InsertRunSql(world.Org, world.Project, parents, Guid.CreateVersion7(), identityOverrides));
    }

    /// <summary>Runs one statement as the migration role (which may update every column) and returns the server error.</summary>
    private async Task<PostgresException> OwnerUpdateErrorAsync(string database, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>The migration file's own SQL, executed as the migration role — the re-run path.</summary>
    private async Task ReRunMigrationAsync(string database)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            await File.ReadAllTextAsync(
                Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile),
                TestContext.Current.CancellationToken));
        await scope.CommitAsync();
    }
}
