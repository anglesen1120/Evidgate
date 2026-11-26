using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application.Evaluation;
using Evidgate.Modules.Policy.Application.Expiry;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The expiry sweep end to end on the real schema: the platform handler pages the due orgs as the sweep role, expires
// each org's due waivers and quarantine entries in its own tenant transaction with one audit row per row, and the next
// evaluation reads the changed snapshot while the stored earlier verdict stays as it was. It extends the evaluation
// suite's partial class on purpose: the run, evidence and exception seeds are that suite's private helpers.
public sealed partial class EvaluationTaskTests
{
    private static readonly TimeSpan SnapshotAge = TimeSpan.FromHours(-2);

    /// <summary>
    /// Due rows expire exactly once with one system audit row each, rows that are not due or already terminal are left
    /// alone, a re-run writes nothing, and the next evaluation's snapshot hash differs from the earlier one whose stored
    /// row is unchanged.
    /// </summary>
    [Fact]
    public async Task Expiry_DueRows_ExpireOnceWithOneAuditRowEachAndTheNextSnapshotDiffers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(
            _fixture, database: dedicated.Name, services: UseSweepRole(dedicated.Name));
        var seed = await SeedAsync(world);
        var rows = await SeedExceptionsAsync(world, seed);

        // The earlier verdict reads the snapshot as it stood two hours ago, when every approved and active row held.
        var asOf = DateTimeOffset.UtcNow + SnapshotAge;
        var prior = await EvaluateAtAsync(world, seed, EvaluationMode.Live, asOf, token);
        prior.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf(
            [rows.DueWaiver, rows.FutureWaiver], [rows.DueQuarantine, rows.FutureQuarantine]));
        var priorRow = await StoredRowTextAsync(world, prior.Id);

        await SweepAsync(world, token);

        var afterSweep = await StatesAsync(world);
        afterSweep.ToDictionary(pair => pair.Key, pair => pair.Value.State).ShouldBe(new Dictionary<Guid, string>
        {
            [rows.DueWaiver] = "expired",
            [rows.DueQuarantine] = "expired",
            [rows.FutureWaiver] = "approved",
            [rows.FutureQuarantine] = "active",
            [rows.RequestedWaiver] = "requested",
            [rows.RevokedWaiver] = "revoked",
            [rows.RemovedQuarantine] = "removed",
        });
        (await AuditRowsAsync(world)).ShouldBe(
            [
                $"quarantine.expire|quarantine|{rows.DueQuarantine:D}|system|system",
                $"waiver.expire|waiver|{rows.DueWaiver:D}|system|system",
            ],
            "one system audit row per transition, none for rows the sweep left alone");
        await VerifyAuditChainAsync(world, seed, token);

        await SweepAsync(world, token);
        (await StatesAsync(world)).ShouldBe(afterSweep, "a re-run finds nothing due, so no row or version moves");
        (await AuditRowsAsync(world)).Count.ShouldBe(2, "and writes no second audit row");

        var next = await EvaluateAtAsync(world, seed, EvaluationMode.Shadow, asOf, token);
        next.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf([rows.FutureWaiver], [rows.FutureQuarantine]));
        next.ExceptionsHash.ShouldNotBe(prior.ExceptionsHash, "the swept rows are out of the next snapshot");
        (await StoredRowTextAsync(world, prior.Id)).ShouldBe(priorRow, "an evaluation row is never rewritten by the sweep");
    }

    /// <summary>
    /// A backlog larger than one batch is drained by the one fire, batch by batch, each row with its own audit row.
    /// </summary>
    [Fact]
    public async Task Expiry_BacklogBeyondOneBatch_ExpiresEveryRowWithAnAuditRowEach()
    {
        var token = TestContext.Current.CancellationToken;
        const int backlog = ExpirySweeps.RowBatch + 5;
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(
            _fixture, database: dedicated.Name, services: UseSweepRole(dedicated.Name));
        await using (var connection = await OwnerAsync(world))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                insert into public.waiver
                  (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
                select gen_random_uuid(), @org_id, @project_id, 'case', gen_random_uuid(), 'backlog', gen_random_uuid(),
                       gen_random_uuid(), 'approved', now() - interval '1 hour'
                  from generate_series(1, {backlog})
                """;
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("project_id", world.Project);
            (await command.ExecuteNonQueryAsync(token)).ShouldBe(backlog);
        }

        await SweepAsync(world, token);

        (await StatesAsync(world)).Values.Count(value => value.State == "expired").ShouldBe(backlog);
        (await AuditRowsAsync(world)).Count.ShouldBe(backlog);
    }

    /// <summary>
    /// One org whose transition fails does not starve the orgs behind it: the others are expired and audited, the failed
    /// org keeps its row approved and unaudited, and the fire throws so the queue retries it.
    /// </summary>
    [Fact]
    public async Task Expiry_OneFailingOrg_OthersStillExpireAndTheFireThrows()
    {
        var token = TestContext.Current.CancellationToken;
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        Guid[] orgs =
        [
            .. Enumerable.Range(0, 3)
                .Select(_ => Guid.CreateVersion7())
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal),
        ];
        foreach (var org in orgs)
        {
            await SeedDueOrgAsync(database, org);
        }

        // A real database fault for the middle org only: the sweep runs as the owner, so the trigger fires for it.
        var failing = orgs[1];
        await _fixture.ExecuteAsync(
            database,
            $"""
            create function public.expiry_probe_refuse() returns trigger language plpgsql as
              $$ begin raise exception 'probe: refused'; end $$;
            create trigger zz_expiry_probe before update on public.waiver
              for each row when (old.org_id = '{failing:D}') execute function public.expiry_probe_refuse();
            """);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddLogging();
        services.AddEvidgateTenancy();
        services.AddSingleton<IWorkerSchedDataSource>(
            _ => new UsageRollupScheduleTests.SweepRoleDataSource(_fixture, database));
        services.AddSingleton<ExceptionExpiryStore>();
        services.AddSingleton<SweepExpiredExceptionsTaskHandler>();
        await using var provider = services.BuildServiceProvider();

        var error = await Should.ThrowAsync<AggregateException>(async () => await provider
            .GetRequiredService<SweepExpiredExceptionsTaskHandler>()
            .HandleAsync(new SweepExpiredExceptionsCommand(), scope: null, token));

        error.InnerExceptions.Count.ShouldBe(1);
        error.Message.ShouldContain(failing.ToString("D"), Case.Sensitive);
        (await _fixture.ColumnAsync<string>(
            database,
            "select org_id::text || '|' || state from public.waiver order by org_id::text"))
            .ShouldBe([.. orgs.Select(org => $"{org:D}|{(org == failing ? "approved" : "expired")}")]);
        (await _fixture.ColumnAsync<string>(
            database,
            "select org_id::text from public.audit_log where action = 'waiver.expire' order by org_id::text"))
            .ShouldBe([.. orgs.Where(org => org != failing).Select(org => org.ToString("D"))]);
    }

    private Action<IServiceCollection> UseSweepRole(string database) =>
        services =>
        {
            // The fixture mints no login for the sweep role; the double switches a superuser connection to it.
            services.RemoveAll<IWorkerSchedDataSource>();
            services.AddSingleton<IWorkerSchedDataSource>(
                _ => new UsageRollupScheduleTests.SweepRoleDataSource(_fixture, database));
        };

    private async Task SeedDueOrgAsync(string database, Guid org)
    {
        var project = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'expiry', 'expiry-{org:N}');
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'pp', 'pp');
            insert into public.waiver
              (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            values ('{Guid.CreateVersion7():D}', '{org:D}', '{project:D}', 'case', '{Guid.CreateVersion7():D}', 'due',
                    '{Guid.CreateVersion7():D}', '{Guid.CreateVersion7():D}', 'approved', now() - interval '1 hour');
            """);
    }

    /// <summary>One run of the production handler over the world's host.</summary>
    private static async Task SweepAsync(RunCreationTests.World world, CancellationToken cancellationToken) =>
        await world.Api.Services.GetRequiredService<SweepExpiredExceptionsTaskHandler>()
            .HandleAsync(new SweepExpiredExceptionsCommand(), scope: null, cancellationToken);

    /// <summary>One handler invocation on its own scope at a chosen instant, committed.</summary>
    private static async Task<EvaluationRecord> EvaluateAtAsync(
        RunCreationTests.World world,
        Seed seed,
        EvaluationMode mode,
        DateTimeOffset instant,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenScopeAsync(world, seed, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var result = await HandlerWithClock(services.ServiceProvider, new CountingClock(instant))
            .Handle(new EvaluateRunCommand(seed.RunId, 1, mode), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return result.Value;
    }

    private static async Task VerifyAuditChainAsync(
        RunCreationTests.World world, Seed seed, CancellationToken cancellationToken)
    {
        await using var scope = await OpenScopeAsync(world, seed, cancellationToken);
        (await new AuditChainVerifier(world.Api.Services.GetRequiredService<ITenantDb>())
            .VerifyAsync(seed.Org, cancellationToken)).ShouldBeNull("the sweep's audit rows extend an intact chain");
    }

    private async Task<ExceptionRows> SeedExceptionsAsync(RunCreationTests.World world, Seed seed)
    {
        Guid[] cases = await CaseIdsAsync(world, seed);
        cases.Length.ShouldBe(2, "the world holds two catalog cases, one active quarantine per case");

        // The not-due rows end ten minutes out: far enough that a run of the test never reaches them, near enough that an
        // early-expiry bug in the due predicate would take them.
        const string due = "now() - interval '1 hour'";
        const string notDue = "now() + interval '10 minutes'";
        return new ExceptionRows(
            DueWaiver: await InsertWaiverAsync(world, seed, "approved", due),
            DueQuarantine: await InsertQuarantineAsync(world, seed, "active", due, cases[0]),
            FutureWaiver: await InsertWaiverAsync(world, seed, "approved", notDue),
            FutureQuarantine: await InsertQuarantineAsync(world, seed, "active", notDue, cases[1]),
            RequestedWaiver: await InsertWaiverAsync(world, seed, "requested", due),
            RevokedWaiver: await InsertWaiverAsync(world, seed, "revoked", due),
            RemovedQuarantine: await InsertQuarantineAsync(world, seed, "removed", due, cases[0]));
    }

    private async Task<Guid[]> CaseIdsAsync(RunCreationTests.World world, Seed seed)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText = "select id from public.test_case where org_id = @org_id order by id";
        command.Parameters.AddWithValue("org_id", seed.Org);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    /// <summary>Every waiver and quarantine entry of the world's org: id to state and version.</summary>
    private async Task<Dictionary<Guid, (string State, int Version)>> StatesAsync(RunCreationTests.World world)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, state, version from public.waiver where org_id = @org_id
            union all
            select id, state, version from public.quarantine_entry where org_id = @org_id
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var states = new Dictionary<Guid, (string State, int Version)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            states[reader.GetGuid(0)] = (reader.GetString(1), reader.GetInt32(2));
        }

        return states;
    }

    private async Task<IReadOnlyList<string>> AuditRowsAsync(RunCreationTests.World world)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select action || '|' || object_type || '|' || object_id || '|' || actor_type || '|' || actor_id
              from public.audit_log
             where org_id = @org_id and action in ('waiver.expire', 'quarantine.expire')
             order by action, object_id
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<string> StoredRowTextAsync(RunCreationTests.World world, Guid evaluationId)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText = "select to_jsonb(e)::text from public.evaluation e where e.id = @id";
        command.Parameters.AddWithValue("id", evaluationId);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed record ExceptionRows(
        Guid DueWaiver,
        Guid DueQuarantine,
        Guid FutureWaiver,
        Guid FutureQuarantine,
        Guid RequestedWaiver,
        Guid RevokedWaiver,
        Guid RemovedQuarantine);
}
