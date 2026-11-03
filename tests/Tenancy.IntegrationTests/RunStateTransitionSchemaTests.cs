// 05.3.1-T1.1/T1.3: migration 0036 through a real PostgreSQL — the nullable evidence pointer and the app
// role's guarded UPDATE grant on `run.state` that the transition needs. 05.3.2-T5's 0049 adds the pointer's
// composite FK and its own column grant, so the assertions below read the schema at head.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.1-T1.1 (0036) through a real PostgreSQL: the two statements the story's migration adds to
/// <c>run</c> — <c>current_evidence_revision_id</c> (a nullable pointer) and
/// <c>grant update (state) on public.run to evidgate_app</c> (0031 revoked table-level UPDATE and granted
/// <c>SELECT, INSERT</c> only; 0034 granted <c>updated_at</c> for the T91 row lock).
/// </summary>
/// <remarks>
/// 0036 deliberately landed the pointer without its composite FK. 05.3.2-T5's 0049 adds
/// <c>(org_id, current_evidence_revision_id) -&gt; evidence_revision (org_id, id)</c> and grants the app
/// role <c>update (current_evidence_revision_id)</c>, so the assertions below are 0036's shape as 0049
/// completes it: the pointer is the third writable column and the FK is the only constraint naming it. The
/// pointer's own behaviour — latest revision of the same run only, never cleared by the app role (the migration role erasure sweep is the exception) — is 0049's and is covered
/// by <see cref="EvidenceRevisionSchemaTests"/>.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class RunStateTransitionSchemaTests
{
    /// <summary>The migration under test, re-run by the existing-row-safety case.</summary>
    private const string MigrationFile = "0036_orchestration_run_state_grant.sql";

    private readonly PostgresTenancyFixture _fixture;

    public RunStateTransitionSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) the migration's shape

    /// <summary>
    /// 0036 adds <c>current_evidence_revision_id</c> as a nullable <c>uuid</c>; 05.3.2-T5's 0049 adds the
    /// composite FK 0036 deferred, so at head the only constraint naming the column is
    /// <c>run_current_evidence_revision_fk (org_id, current_evidence_revision_id) -&gt; evidence_revision
    /// (org_id, id)</c>. NULL stays legal (MATCH SIMPLE, a run without a frozen revision).
    /// </summary>
    [Fact]
    public async Task Migration0036_AddsTheNullablePointer_WithThe0049CompositeForeignKey()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

        (await _fixture.ScalarAsync<bool>(
            database,
            """
            select exists (
              select 1 from information_schema.columns
               where table_schema = 'public' and table_name = 'run'
                 and column_name = 'current_evidence_revision_id'
                 and data_type = 'uuid' and is_nullable = 'YES')
            """))
            .ShouldBeTrue("the pointer column is a nullable uuid");

        // One constraint of `run` names the pointer, and it is 0049's FK onto evidence_revision's tenant key.
        var pointerConstraint = (await _fixture.ConstraintsAsync(database, "run"))
            .Where(constraint => constraint.Contains("current_evidence_revision_id", StringComparison.Ordinal))
            .ToArray()
            .ShouldHaveSingleItem();

        pointerConstraint.ShouldStartWith("f:run_current_evidence_revision_fk:");
        pointerConstraint.ShouldContain("FOREIGN KEY (org_id, current_evidence_revision_id)");
        pointerConstraint.ShouldContain("REFERENCES evidence_revision(org_id, id)");
    }

    /// <summary>
    /// The app role's UPDATE grants on <c>run</c> are exactly the columns the module's writes name:
    /// <c>updated_at</c> (0034's T91 row lock, assigned by the shared version trigger), <c>state</c> (0036,
    /// 05.3.1's guarded transition) and <c>current_evidence_revision_id</c> (0049, 05.3.2-T5's guarded
    /// pointer move). <c>purpose</c>, <c>replay_grant_id</c>, <c>version</c> and <c>ci_run_ref</c> stay
    /// outside — a column grant is not a table grant, so table-level UPDATE and DELETE stay revoked.
    /// </summary>
    [Fact]
    public async Task Migration0036_GrantsUpdateOnTheStateColumnOnly()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

        (await _fixture.ColumnAsync<string>(
            database,
            """
            select column_name
              from information_schema.columns
             where table_schema = 'public' and table_name = 'run'
               and has_column_privilege('evidgate_app', 'public.run', column_name, 'UPDATE')
             order by column_name
            """))
            .ShouldBe(["current_evidence_revision_id", "state", "updated_at"]);

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run", "update")).ShouldBeFalse("a column grant is not a table grant");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run", "delete")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run", "insert")).ShouldBeTrue();

        // The operator keeps its read grant and gains nothing writable.
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", "run", "update")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_operator', 'public.run', 'state', 'update')"))
            .ShouldBeFalse();
    }

    /// <summary>
    /// The grants in behaviour: the app role can move <c>state</c> on its own tenant's run, and every column
    /// outside the three grants is still 42501 — 0049's pointer is writable since 05.3.2-T5, but only through
    /// its own guard (covered by <see cref="EvidenceRevisionSchemaTests"/>).
    /// </summary>
    [Fact]
    public async Task AppRole_MovesState_AndNothingElseOnRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        var runId = created.Id("run_id");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.run set state = 'running' where id = '{runId}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId}'")).ShouldBe("running");

        foreach (var column in new[]
                 {
                     "purpose = 'replay'",
                     "replay_grant_id = null",
                     "version = 9",
                     "ci_run_ref = '{}'::jsonb",
                 })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

            (await scope.CaptureErrorAsync($"update public.run set {column} where id = '{runId}'")).SqlState.ShouldBe("42501", column);
        }

        (await _fixture.ScalarAsync<string>(world.Database, $"select purpose || ':' || version::text from public.run where id = '{runId}'"))
            .ShouldBe("regular:1", "no un-granted column moved; the 0004 trigger bumped version once for the state update");
    }

    // ------------------------------------------------------------------ (b) the migration re-run

    /// <summary>
    /// The file is idempotent (<c>add column if not exists</c> plus a repeated <c>grant</c>): re-running its
    /// own SQL as the migration role leaves the column and the grant in place and adds nothing.
    /// </summary>
    [Fact]
    public async Task Migration0036_ReRun_KeepsTheColumnAndTheGrant()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

        await ReRunMigrationAsync(database);

        (await _fixture.ColumnAsync<string>(
            database,
            "select column_name from information_schema.columns where table_schema = 'public' and table_name = 'run' "
            + "and column_name = 'current_evidence_revision_id'"))
            .ShouldBe(["current_evidence_revision_id"]);
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.run', 'state', 'update')"))
            .ShouldBeTrue();
    }

    /// <summary>The migration file's own SQL, executed as the migration role — the re-run path.</summary>
    private async Task ReRunMigrationAsync(string database)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(await MigrationSqlAsync());
        await scope.CommitAsync();
    }

    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile),
            TestContext.Current.CancellationToken);
}
