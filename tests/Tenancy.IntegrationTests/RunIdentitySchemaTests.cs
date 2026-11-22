using Shouldly;
using Xunit;

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
/// PostgreSQL 18 container per assembly and one database for the run (tests isolate by tenant), with a
/// private database only where a case re-applies the migration. Parents and the row itself are written
/// through <see cref="TenantScope"/>, the parents as the migration role (the owner backfill policy of
/// 0004/0005 permits it) and the run under test as the app role, exactly like <c>RunSchemaTests</c>.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class RunIdentitySchemaTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public RunIdentitySchemaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    // ------------------------------------------------------------------ (a) migration: shape, function, trigger

    [Fact]
    public async Task Identity_ConstraintsFunctionAndTrigger_Exist()
    {
        var database = await _fixture.SharedDatabaseAsync();

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
        var database = await _fixture.SharedDatabaseAsync();

        // 0037 adds columns, a function and a trigger to `run`; the table-scoped policy and the two
        // row-security flags 0031 set are untouched (00 §5.4, DECISIONS F5).
        (await _fixture.RowSecurityAsync(database, "run")).ShouldBe((true, true));
    }

    [Fact]
    public async Task CiTokenIssueRunIndex_ExistsOnTheRunReferenceKeys_AndSurvivesTheReRun()
    {
        // Dedicated: it re-applies the migration, so the index must be counted in a database it alone owns.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

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
        // Dedicated: it re-applies the migration and counts the run table whole, so it owns the database.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
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
