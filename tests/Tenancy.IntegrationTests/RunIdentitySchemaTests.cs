using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1.2 through a real PostgreSQL: migration 0037's <c>run</c> identity group on top of 0031's
/// table — AC4's shape constraints (`ref_kind`, `ref_name`, `pr_number`, `head_sha`/`base_sha`,
/// `trigger_event` and the `pull_request ⇒ pr_number` cross-column rule), AC5's
/// <c>run_identity_immutable</c> trigger (SQLSTATE <c>P0001</c> for any change to the eight identity
/// columns, on a row whose non-identity columns stay writable), the two <c>not null default false</c>
/// flags, and the RLS flags 0031 set, which this file must leave alone.
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned
/// PostgreSQL 18 container per assembly, a fresh database per test brought to head by the real
/// <c>Evidgate.Migrator</c> — the 05.2.1-T1 / 05.3.3-T1 precedent for adding to an existing table in
/// this suite: parents and the row itself are written through <see cref="TenantScope"/>, the parents as
/// the migration role (the owner backfill policy of 0004/0005 permits it) and the run under test as the
/// app role, exactly like <c>RunSchemaTests</c>.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunIdentitySchemaTests
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

    private readonly PostgresTenancyFixture _fixture;

    public RunIdentitySchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>
    /// The parent rows a <c>run</c> row needs: the manifest with its suite and environment revisions.
    /// No test case or test revision is seeded — this story's table is <c>run</c> alone, and
    /// <c>planned_execution</c> (the only consumer of a test revision) is not written here.
    /// </summary>
    private sealed record Parents(Guid SuiteRevisionId, Guid EnvironmentRevisionId, Guid ManifestId);

    /// <summary>One migrated database with an org and a project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
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

    // ------------------------------------------------------------------ (a) migration: shape, function, trigger

    [Fact]
    public async Task Identity_ConstraintsFunctionAndTrigger_Exist()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var constraints = await _fixture.ConstraintNamesAsync(database, "run");

        foreach (var name in new[]
                 {
                     "run_ref_kind_check", "run_ref_name_check", "run_pr_number_check", "run_head_sha_check",
                     "run_base_sha_check", "run_trigger_event_check", "run_pr_requires_number",
                 })
        {
            constraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }

        (await _fixture.ScalarAsync<bool>(
            database,
            "select exists (select 1 from pg_catalog.pg_proc where proname = 'forbid_run_identity_update' and pronamespace = 'public'::regnamespace)"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select exists (select 1 from pg_catalog.pg_trigger where tgrelid = 'public.run'::regclass and tgname = 'run_identity_immutable')"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Run_KeepsItsForcedRls()
    {
        var database = await _fixture.CreateDatabaseAsync();

        // 0037 adds columns, a function and a trigger to `run`; the table-scoped policy and the two
        // row-security flags 0031 set are untouched (00 §5.4, DECISIONS F5).
        (await _fixture.RowSecurityAsync(database, "run")).ShouldBe((true, true));
    }

    [Fact]
    public async Task CiTokenIssueRunIndex_ExistsOnTheRunReferenceKeys_AndSurvivesTheReRun()
    {
        var database = await _fixture.CreateDatabaseAsync();

        async Task<string> DefinitionAsync() =>
            (await _fixture.IndexesAsync(database, "ci_token_issue"))
                .SingleOrDefault(index => index.StartsWith("ci_token_issue_run_idx:", StringComparison.Ordinal))
            ?? string.Empty;

        // D-S45-Q2: the verified-caller read filters (project_id, repository_id, run_id, run_attempt) and
        // takes the newest row of the attempt (created_at desc), so 0037 adds exactly this btree index to
        // the Identity-owned `ci_token_issue` — no grant, policy or RLS change.
        var definition = await DefinitionAsync();
        definition.ShouldContain("ON public.ci_token_issue USING btree (project_id, repository_id, run_id, run_attempt, created_at DESC)");

        // `create index if not exists`: the second application of 0037 must not fail on the index it
        // already created and must leave exactly that one behind.
        await ReRunMigrationAsync(database);

        (await DefinitionAsync()).ShouldBe(definition);
        (await _fixture.IndexesAsync(database, "ci_token_issue"))
            .Count(index => index.StartsWith("ci_token_issue_run_idx:", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ (b) AC4: the shape constraints

    [Theory]
    [MemberData(nameof(BadIdentityShapes))]
    public async Task Identity_OutsideTheBound_FailsCheck(string column, string expression, string constraint)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await InsertRunExpectingErrorAsync(world, parents, (column, expression));

        error.SqlState.ShouldBe("23514", $"{column} = {expression}");
        error.ConstraintName.ShouldBe(constraint, $"{column} = {expression}");
    }

    [Fact]
    public async Task PullRequestRun_WithoutPrNumber_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        // AC4's cross-column rule: a pull request names its number.
        var error = await InsertRunExpectingErrorAsync(
            world, parents, ("ref_kind", "'pull_request'"), ("pr_number", "null"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_pr_requires_number");
    }

    [Theory]
    [MemberData(nameof(RefKinds))]
    public async Task RefKind_AnyVocabularyValue_IsAccepted(string refKind, string prNumber)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(
            world, parents, Guid.CreateVersion7(), ("ref_kind", refKind), ("pr_number", prNumber));

        (await _fixture.ScalarAsync<string>(world.Database, $"select ref_kind from public.run where id = '{runId}'"))
            .ShouldBe(refKind.Trim('\''));
    }

    [Theory]
    [MemberData(nameof(TriggerEvents))]
    public async Task TriggerEvent_AnyVocabularyValue_IsAccepted(string triggerEvent)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), ("trigger_event", triggerEvent));

        (await _fixture.ScalarAsync<string>(world.Database, $"select trigger_event from public.run where id = '{runId}'"))
            .ShouldBe(triggerEvent.Trim('\''));
    }

    [Theory]
    [MemberData(nameof(AcceptedShaShapes))]
    public async Task Sha_BothAcceptedLengths_AreAccepted(string column, string expression)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), (column, expression));

        // 40 and 64 hex: the SHA-1 shape and the SHA-256 repository shape, both lowercase.
        (await _fixture.ScalarAsync<string>(world.Database, $"select {column} from public.run where id = '{runId}'"))
            .ShouldBe(expression.Trim('\''));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public async Task RefName_WithinTheBound_IsAccepted(int length)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), ("ref_name", $"repeat('a', {length})"));

        (await _fixture.ScalarAsync<int>(world.Database, $"select char_length(ref_name) from public.run where id = '{runId}'"))
            .ShouldBe(length);
    }

    // ------------------------------------------------------------------ (c) the stored row

    [Fact]
    public async Task FullIdentityRow_InsertsAndReadsBack()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), FullIdentity);

        (await _fixture.ScalarAsync<string>(world.Database, $"select ref_kind from public.run where id = '{runId}'"))
            .ShouldBe("pull_request");
        (await _fixture.ScalarAsync<string>(world.Database, $"select ref_name from public.run where id = '{runId}'"))
            .ShouldBe("refs/pull/42/merge");
        (await _fixture.ScalarAsync<int>(world.Database, $"select pr_number from public.run where id = '{runId}'"))
            .ShouldBe(42);
        (await _fixture.ScalarAsync<string>(world.Database, $"select head_sha from public.run where id = '{runId}'"))
            .ShouldBe(FortyHex);
        (await _fixture.ScalarAsync<string>(world.Database, $"select base_sha from public.run where id = '{runId}'"))
            .ShouldBe(SixtyFourHex);
        (await _fixture.ScalarAsync<string>(world.Database, $"select trigger_event from public.run where id = '{runId}'"))
            .ShouldBe("pull_request");
        (await _fixture.ScalarAsync<bool>(world.Database, $"select is_fork from public.run where id = '{runId}'"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(world.Database, $"select unverified_ref from public.run where id = '{runId}'"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task RunWithoutIdentity_KeepsNullsAndFalseFlags()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        // The pre-05.2.2 row shape (plan §4, "existing rows"): every identity column omitted, the two
        // flags defaulted. Inserted with no identity override at all, so the column defaults are what is
        // under test.
        var runId = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(
                "insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes) "
                + $"values ('{runId}', '{world.Org}', '{world.Project}', '{parents.ManifestId}', '{parents.SuiteRevisionId}', '{parents.EnvironmentRevisionId}', now() + interval '2 hours', 1440)"))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<string?>(world.Database, $"select ref_kind from public.run where id = '{runId}'"))
            .ShouldBeNull();
        (await _fixture.ScalarAsync<string?>(world.Database, $"select ref_name from public.run where id = '{runId}'"))
            .ShouldBeNull();
        (await _fixture.ScalarAsync<string?>(world.Database, $"select head_sha from public.run where id = '{runId}'"))
            .ShouldBeNull();
        (await _fixture.ScalarAsync<bool>(world.Database, $"select is_fork from public.run where id = '{runId}'"))
            .ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(world.Database, $"select unverified_ref from public.run where id = '{runId}'"))
            .ShouldBeFalse();
    }

    // ------------------------------------------------------------------ (d) AC5: the immutability trigger

    [Theory]
    [MemberData(nameof(ChangedIdentityValues))]
    public async Task IdentityColumn_DirectUpdate_AsOwner_FailsWithP0001(string column, string expression)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), FullIdentity);

        var error = await OwnerUpdateErrorAsync(
            world.Database, $"update public.run set {column} = {expression} where id = '{runId}'");

        error.SqlState.ShouldBe("P0001", column);
        error.MessageText.ShouldContain("run_identity_immutable");

        // The row is unchanged after the refusal.
        (await _fixture.ScalarAsync<string>(world.Database, $"select ref_name from public.run where id = '{runId}'"))
            .ShouldBe("refs/pull/42/merge");
    }

    [Fact]
    public async Task IdentityColumn_SetToItsOwnValue_Succeeds()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), FullIdentity);

        // Only a *difference* is refused: a statement that names an identity column without changing it
        // (an EF Core full-row UPDATE, say) still goes through.
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            $"update public.run set head_sha = head_sha, ref_kind = ref_kind where id = '{runId}'")).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task NonIdentityColumn_Update_SucceedsOnTheSameRow()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), FullIdentity);

        // The owner's write: the identity trigger returns NEW untouched, so the shared
        // `set_updated_at_and_version` trigger of 0031 still owns version/updated_at.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync($"update public.run set state = 'queued' where id = '{runId}'")).ShouldBe(1);
            (await owner.ScalarAsync<string>($"select state from public.run where id = '{runId}'")).ShouldBe("queued");
            (await owner.ScalarAsync<int>($"select version from public.run where id = '{runId}'")).ShouldBe(1);
            await owner.CommitAsync();
        }

        // And the real app-role path of 05.3.1: `grant update (state)` (0036) plus the new trigger.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await app.ExecuteAsync($"update public.run set state = 'running' where id = '{runId}'")).ShouldBe(1);
            (await app.ScalarAsync<string>($"select state from public.run where id = '{runId}'")).ShouldBe("running");
            (await app.ScalarAsync<bool>($"select unverified_ref from public.run where id = '{runId}'")).ShouldBeTrue();
            await app.CommitAsync();
        }

        // A non-identity jsonb column too: nothing in the identity guard is column-list-scoped to `state`.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync(
                $"update public.run set components = '{{\"key\":\"value\"}}'::jsonb where id = '{runId}'")).ShouldBe(1);
            await owner.CommitAsync();
        }
    }

    [Fact]
    public async Task AppRole_CannotNameAnIdentityColumn_AtThePrivilegeCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), FullIdentity);

        // The header's "defense in depth": 0031 revoked table-level UPDATE and the later column grants
        // are `updated_at` (0034) and `state` (0036) only, so the app role is refused before the trigger.
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.CaptureErrorAsync($"update public.run set ref_kind = 'tag' where id = '{runId}'"))
            .SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ (e) the migration re-run

    [Fact]
    public async Task Migration_ReRun_IsANoOpAndKeepsTheGuard()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents, Guid.CreateVersion7(), FullIdentity);

        // `add column if not exists` / `drop constraint if exists` + `add constraint` /
        // `create or replace function` / `drop trigger if exists` + `create trigger`: the second pass
        // must change nothing and must leave an enforcing trigger behind.
        await ReRunMigrationAsync(world.Database);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run")).ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select exists (select 1 from pg_catalog.pg_trigger where tgrelid = 'public.run'::regclass and tgname = 'run_identity_immutable')"))
            .ShouldBeTrue();

        var error = await OwnerUpdateErrorAsync(
            world.Database, $"update public.run set ref_name = 'other' where id = '{runId}'");

        error.SqlState.ShouldBe("P0001");
        (await _fixture.ScalarAsync<string>(world.Database, $"select ref_name from public.run where id = '{runId}'"))
            .ShouldBe("refs/pull/42/merge");
    }
}
