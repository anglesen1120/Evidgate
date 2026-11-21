using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRevisionSchemaTests
{
    // ------------------------------------------------------------------ helpers

    /// <summary>One run through the served route; returns its id.</summary>
    private static async Task<Guid> CreateRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        return created.Id("run_id");
    }

    /// <summary>A second org (and the project its runs hang off) through the real registry routes.</summary>
    private static async Task<Guid> CreateSecondTenantAsync(RunCreationTests.World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        var org = await CreateOrgAsync(world.Client, user, unique);
        await CreateProjectAsync(world.Client, user, org, unique + "-project");

        return org;
    }

    /// <summary>Appends one revision as the app role in its own committed transaction.</summary>
    private async Task<Guid> AppendRevisionAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        int revisionNo,
        string completeness = "complete",
        string missing = "{}",
        string? outcome = "all_passed")
    {
        var id = Guid.CreateVersion7();
        var insert = RevisionInsert(id, org, runId, revisionNo, completeness, missing, outcome);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>
    /// One concurrent writer (D-S115-Q1): the run row locked <c>FOR NO KEY UPDATE</c> first, then
    /// <c>max + 1</c> read under that lock, then the insert, then the commit. A refusal or a deadlock is
    /// returned rather than thrown so the assertion names it.
    /// </summary>
    private async Task<(int RevisionNo, PostgresException? Error)> AppendNextRevisionAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        try
        {
            (await scope.ScalarAsync<int>(
                "select 1 from public.run where org_id = @org_id and id = @run_id for no key update",
                ("org_id", org),
                ("run_id", runId)))
                .ShouldBe(1);

            var revisionNo = await scope.ScalarAsync<int>(
                "select coalesce(max(revision_no), 0) + 1 from public.evidence_revision "
                + "where org_id = @org_id and run_id = @run_id",
                ("org_id", org),
                ("run_id", runId));

            var insert = RevisionInsert(Guid.CreateVersion7(), org, runId, revisionNo);
            (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
            await scope.CommitAsync();

            return (revisionNo, null);
        }
        catch (PostgresException error)
        {
            return (0, error);
        }
    }

    /// <summary>Runs one pointer UPDATE as the app role and returns the affected row count.</summary>
    private async Task<int> MovePointerAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        Guid? revisionId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        var affected = await scope.ExecuteAsync(
            "update public.run set current_evidence_revision_id = @revision_id where org_id = @org_id and id = @run_id",
            ("revision_id", revisionId is { } id ? (object)id : DBNull.Value),
            ("org_id", org),
            ("run_id", runId));
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>Runs one pointer UPDATE as the app role and returns the server error.</summary>
    private async Task<PostgresException> MovePointerErrorAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        Guid? revisionId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(
            "update public.run set current_evidence_revision_id = @revision_id where org_id = @org_id and id = @run_id",
            ("revision_id", revisionId is { } id ? (object)id : DBNull.Value),
            ("org_id", org),
            ("run_id", runId));
    }

    /// <summary>
    /// Polls <c>pg_stat_activity</c> until the backend <paramref name="blockedPid"/> waits on a lock held by
    /// <paramref name="holderPid"/> (<c>pg_blocking_pids</c> resolves the transaction-id wait a row lock shows
    /// as), so a test observes the block instead of sleeping and hoping. Returns false on timeout; the caller
    /// asserts on that.
    /// </summary>
    private async Task<bool> WaitForLockWaitAsync(string database, int blockedPid, int holderPid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_stat_activity "
                + $"where pid = {blockedPid} and wait_event_type = 'Lock' "
                + $"and array_position(pg_blocking_pids(pid), {holderPid}) is not null");
            if (waiting == 1)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        return false;
    }

    /// <summary>One run row of the same org, copied from an existing run through the app role; the pointer is overridable.</summary>
    private const string RunCopyInsert =
        """
        insert into public.run
          (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
           collection_deadline_at, late_window_minutes, current_evidence_revision_id)
        select @id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes, @revision_id::uuid
          from public.run
         where org_id = @org_id and id = @template_run_id
        """;

    /// <summary>Writes one copy of <paramref name="template"/> as the app role and returns the affected row count.</summary>
    private async Task<int> InsertRunCopyAsync(
        RunCreationTests.World world,
        Guid id,
        Guid template,
        Guid? revision)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var affected = await scope.ExecuteAsync(RunCopyInsert, RunCopyParameters(id, world.Org, template, revision));
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>Writes one copy of <paramref name="template"/> as the app role and returns the server error.</summary>
    private async Task<PostgresException> InsertRunCopyErrorAsync(
        RunCreationTests.World world,
        Guid id,
        Guid template,
        Guid? revision)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        return await scope.CaptureErrorAsync(RunCopyInsert, RunCopyParameters(id, world.Org, template, revision));
    }

    private static (string Name, object Value)[] RunCopyParameters(Guid id, Guid org, Guid template, Guid? revision) =>
    [
        ("id", id),
        ("org_id", org),
        ("template_run_id", template),
        ("revision_id", revision is { } value ? (object)value : DBNull.Value),
    ];

    /// <summary>
    /// Runs one INSERT as the app role in a fresh transaction and returns the server error.
    /// <paramref name="rowOrg"/> defaults to the scope's org; a different value writes a row that claims
    /// another tenant.
    /// </summary>
    private async Task<PostgresException> CaptureInsertErrorAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        int revisionNo,
        string completeness = "complete",
        string missing = "{}",
        Guid? rowOrg = null,
        long eventWatermark = 0,
        string? missingExpression = null)
    {
        var insert = RevisionInsert(
            Guid.CreateVersion7(),
            rowOrg ?? org,
            runId,
            revisionNo,
            completeness,
            missing,
            eventWatermark: eventWatermark,
            missingExpression: missingExpression);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(insert.Sql, insert.Parameters);
    }

    /// <summary>Runs one statement as the app role inside a fresh tenant scope and returns the server error.</summary>
    private async Task<PostgresException> CaptureAppErrorAsync(
        RunCreationTests.World world,
        Guid org,
        string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>Runs one scalar read as the app role for <paramref name="org"/>.</summary>
    private async Task<T> ScalarAsOrgAsync<T>(RunCreationTests.World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.ScalarAsync<T>(sql);
    }

    /// <summary>How many non-internal triggers of one name sit on the table.</summary>
    private Task<long> TriggerCountAsync(string database, string table, string trigger) =>
        _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from pg_catalog.pg_trigger where tgrelid = '{table}'::regclass and tgname = '{trigger}' and not tgisinternal");

    /// <summary>
    /// One raw INSERT of an <c>evidence_revision</c> row as the app role; the bounded columns are overridable.
    /// <paramref name="missingExpression"/> replaces the quoted array literal (a nested array needs the
    /// <c>array[…]</c> constructor, which no literal expresses).
    /// </summary>
    private static (string Sql, (string Name, object Value)[] Parameters) RevisionInsert(
        Guid id,
        Guid org,
        Guid runId,
        int revisionNo,
        string completeness = "complete",
        string missing = "{}",
        string? outcome = "all_passed",
        long eventWatermark = 0,
        string? missingExpression = null)
    {
        var missingSql = missingExpression ?? $"'{missing}'::text[]";
        var sql =
            $"""
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values (@id, @org_id, @run_id, @revision_no, @completeness, {missingSql}, @outcome, @event_watermark, @manifest_hash)
            """;

        return (sql,
        [
            ("id", id),
            ("org_id", org),
            ("run_id", runId),
            ("revision_no", revisionNo),
            ("completeness", completeness),
            ("outcome", (object?)outcome ?? DBNull.Value),
            ("event_watermark", eventWatermark),
            ("manifest_hash", ManifestHash),
        ]);
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
