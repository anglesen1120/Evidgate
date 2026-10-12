// 05.2.1-T4: the CI-mode shard expectations and the created -> queued transition, through the real host and a real
// PostgreSQL. Request bodies are built inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T4 through the real host and a real PostgreSQL: the create transaction derives one <c>producer_slot</c> per
/// manifest shard (<c>shard-1..shard-N</c>, indexes 1..N) and writes <c>planned_execution.shard_id</c> as the same
/// <c>shard-&lt;ordinal&gt;</c> string, and the run row is written <c>queued</c> inside that same transaction — a
/// recording trigger on <c>public.run</c> records every state the handler writes (only <c>queued</c>, never <c>created</c>)
/// (T4.1/T4.2).
/// </summary>
/// <remarks>
/// The boundary tests <c>CreateRun_ShardCountEqualsPlannedCount_OneSlotPerShard</c> and
/// <c>CreateRun_TwoRunsFromOneManifest_IndependentSlotSets</c> were added by the QA agent.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunCreationShardStateTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunCreationShardStateTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The 00 §8.1 run-state vocabulary, the exact set the migration's <c>run_state_check</c> enumerates.</summary>
    private static readonly string[] Vocabulary =
        ["created", "queued", "running", "collecting", "complete", "incomplete", "canceled"];

    // ------------------------------------------------------------------ T4.1 shard expectations

    /// <summary>
    /// AC (T4.1): a manifest with an UNEVEN shard plan (5 planned executions over 3 shards -> ranges 2, 2, 1) yields
    /// exactly one slot per shard, <c>shard-1..shard-3</c> with indexes 1..3, every slot on the created run, and the
    /// planned executions carry the same <c>shard-&lt;ordinal&gt;</c> ids with one row per planned execution in each
    /// shard's range. The expected sizes come from the stored manifest's shard plan, not from a second literal.
    /// </summary>
    [Fact]
    public async Task CreateRun_ShardExpectations_MatchManifestShardPlan()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 5);
        var manifestId = await UploadAsync(world, variants: 1, shards: 3);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(manifestId));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        var expected = await ManifestShardSizesAsync(world, manifestId);
        expected.ShouldBe(["shard-1:2", "shard-2:2", "shard-3:1"], "the fixture exercises an uneven shard plan");

        // One slot per manifest shard, ids shard-1..shard-3, indexes 1..3, every slot on this run.
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.producer_slot")).ShouldBe(expected.Count);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where run_id = '{runId}'"))
            .ShouldBe(expected.Count);
        (await _fixture.ColumnAsync<string>(world.Database, "select producer_id from public.producer_slot order by shard_index"))
            .ShouldBe(["shard-1", "shard-2", "shard-3"]);
        (await _fixture.ColumnAsync<int>(world.Database, "select shard_index from public.producer_slot order by shard_index"))
            .ShouldBe([1, 2, 3]);

        // planned_execution.shard_id is exactly the slot id set, and each shard holds its manifest range's size.
        var planned = await PlannedPerShardAsync(world, runId);
        planned.ShouldBe(expected);
        (await _fixture.ColumnAsync<string>(world.Database, "select distinct shard_id from public.planned_execution order by shard_id"))
            .ShouldBe(await _fixture.ColumnAsync<string>(world.Database, "select producer_id from public.producer_slot order by producer_id"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where run_id = '{runId}'"))
            .ShouldBe(5);
    }

    /// <summary>AC (T4.1): a single-shard manifest registers exactly one slot, <c>shard-1</c> with index 1.</summary>
    [Fact]
    public async Task CreateRun_ShardExpectations_SingleShard_OneSlot()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 2);
        var manifestId = await UploadAsync(world, variants: 1, shards: 1);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(manifestId));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        (await ManifestShardSizesAsync(world, manifestId)).ShouldBe(["shard-1:2"]);
        (await _fixture.ColumnAsync<string>(world.Database, $"select producer_id from public.producer_slot where run_id = '{runId}'"))
            .ShouldBe(["shard-1"]);
        (await _fixture.ColumnAsync<int>(world.Database, $"select shard_index from public.producer_slot where run_id = '{runId}'"))
            .ShouldBe([1]);
        (await PlannedPerShardAsync(world, runId)).ShouldBe(["shard-1:2"]);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.producer_slot")).ShouldBe(1);
    }

    /// <summary>
    /// AC (T4.1) boundary: the manifest builder emits one shard per planned execution when the shard count equals the
    /// planned count (4 cases x 1 variant -> 4 shards, ranges 0..1, 1..2, 2..3, 3..4). Each shard is a single row, so
    /// the derived set is <c>shard-1..shard-4</c> with indexes 1..4 and one planned execution per shard.
    /// </summary>
    [Fact]
    public async Task CreateRun_ShardCountEqualsPlannedCount_OneSlotPerShard()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 4);
        var manifestId = await UploadAsync(world, variants: 1, shards: 4);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(manifestId));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        (await ManifestShardSizesAsync(world, manifestId)).ShouldBe(["shard-1:1", "shard-2:1", "shard-3:1", "shard-4:1"]);
        (await _fixture.ColumnAsync<string>(world.Database, $"select producer_id from public.producer_slot where run_id = '{runId}' order by shard_index"))
            .ShouldBe(["shard-1", "shard-2", "shard-3", "shard-4"]);
        (await _fixture.ColumnAsync<int>(world.Database, $"select shard_index from public.producer_slot where run_id = '{runId}' order by shard_index"))
            .ShouldBe([1, 2, 3, 4]);
        (await PlannedPerShardAsync(world, runId)).ShouldBe(["shard-1:1", "shard-2:1", "shard-3:1", "shard-4:1"]);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.producer_slot")).ShouldBe(4);
    }

    /// <summary>
    /// AC1 (T4.1/T4.2): two creates from the same manifest are two runs with two independent slot sets — distinct
    /// <c>run_id</c>s, one <c>shard-1..shard-3</c> set each, no shared row, and <c>unique (org_id, run_id, producer_id)</c>
    /// holds (6 slots, 6 distinct <c>(run_id, producer_id)</c> pairs).
    /// </summary>
    [Fact]
    public async Task CreateRun_TwoRunsFromOneManifest_IndependentSlotSets()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 5);
        var manifestId = await UploadAsync(world, variants: 1, shards: 3);
        var body = RunCreationTests.RunBody(manifestId);

        var first = await RunCreationTests.CreateRunAsync(world, body);
        var second = await RunCreationTests.CreateRunAsync(world, body);

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        var firstId = first.Id("run_id");
        var secondId = second.Id("run_id");
        firstId.ShouldNotBe(secondId);

        foreach (var runId in new[] { firstId, secondId })
        {
            (await _fixture.ColumnAsync<string>(world.Database, $"select producer_id from public.producer_slot where run_id = '{runId}' order by shard_index"))
                .ShouldBe(["shard-1", "shard-2", "shard-3"]);
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.producer_slot")).ShouldBe(6);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(distinct (run_id, producer_id)) from public.producer_slot")).ShouldBe(6);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where run_id not in ('{firstId}', '{secondId}')")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ T4.2 created -> queued

    /// <summary>
    /// AC7 (T4.2): the run is written <c>queued</c> in the slots' transaction. A recording trigger on <c>public.run</c>
    /// captures every state the row ever holds, so the probe table proves <c>created</c> was never persisted at any
    /// point inside the transaction — across four creates — and that the idempotent replay adds no row at all.
    /// </summary>
    [Fact]
    public async Task CreateRun_StateIsQueuedAfterCommit()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await InstallStateProbeAsync(world.Database);
        (await ProbeStatesAsync(world)).ShouldBeEmpty("the trigger is installed before the first run exists");

        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);
        var first = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Text("state").ShouldBe("queued");
        var runId = first.Id("run_id");

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId}'")).ShouldBe("queued");
        (await ProbeStatesAsync(world)).ShouldBe(["queued"], "the row was written queued; created was never persisted");

        // Three more creates, each with a fresh key: one probe row each, all queued, and no run in any other state.
        var states = new List<string> { first.Text("state")! };
        for (var i = 0; i < 3; i++)
        {
            var created = await RunCreationTests.CreateRunAsync(world, body);
            created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
            states.Add(created.Text("state")!);
        }

        states.ShouldAllBe(state => state == "queued");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run")).ShouldBe(4);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run where state <> 'queued'")).ShouldBe(0);
        (await ProbeStatesAsync(world)).ShouldBe(["queued", "queued", "queued", "queued"]);

        // The idempotent replay returns the stored 201 and writes no run and no probe row.
        var replay = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Text("state").ShouldBe("queued");
        replay.Id("run_id").ShouldBe(runId);
        (await ProbeStatesAsync(world)).ShouldBe(["queued", "queued", "queued", "queued"]);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run")).ShouldBe(4);
    }

    /// <summary>
    /// The <c>run_state_check</c> CHECK enumerates exactly the 00 §8.1 vocabulary (read from
    /// <c>pg_get_constraintdef</c>, so an added or dropped literal fails here), accepts every one of those literals and
    /// refuses a value outside the set — all inside a transaction that is rolled back, so no row survives.
    /// </summary>
    /// <remarks>
    /// The literal comparison is order-sensitive on purpose: a reorder of the literals in the migration must be
    /// mirrored here. App-role refusal of a bad state is covered by <c>RunSchemaTests.State_Unknown_FailsCheck</c>.
    /// </remarks>
    [Fact]
    public async Task CreateRun_StateCheckConstraint_EnumeratesTheFullVocabulary()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var definition = await _fixture.ScalarAsync<string>(
            world.Database,
            """
            select pg_get_constraintdef(con.oid)
              from pg_constraint con
             where con.conrelid = 'public.run'::regclass
               and con.conname = 'run_state_check'
            """);

        StateLiterals(definition).ShouldBe(Vocabulary);

        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        // Every literal of the vocabulary is accepted ...
        foreach (var state in Vocabulary)
        {
            await InsertRunAsync(connection, transaction, world, state);
        }

        // ... and a value outside it is refused by the same constraint.
        var refusal = await Should.ThrowAsync<PostgresException>(() => InsertRunAsync(connection, transaction, world, "finished"));
        refusal.SqlState.ShouldBe("23514");
        refusal.ConstraintName.ShouldBe("run_state_check");

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Uploads a manifest over the world's suite revision and cases with the environment revision set, and returns its
    /// id. The manifest and upload helpers live on <see cref="RunCreationTests"/>.
    /// </summary>
    private static Task<Guid> UploadAsync(RunCreationTests.World world, int variants, int shards)
    {
        var manifest = RunCreationTests.Manifest(world.SuiteRevision, world.Cases, variants, shards);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();
        return RunCreationTests.UploadAsync(world.Client, world.Admin, world.Project, manifest);
    }

    /// <summary>The stored manifest's shard plan as <c>shard-&lt;ordinal&gt;:&lt;size&gt;</c>, ordered by ordinal.</summary>
    private Task<IReadOnlyList<string>> ManifestShardSizesAsync(RunCreationTests.World world, Guid manifestId) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select 'shard-' || s.ordinal::text || ':' || ((shard ->> 'to_index')::int - (shard ->> 'from_index')::int)::text
              from public.manifest m
              cross join lateral jsonb_array_elements(m.document -> 'shard_plan' -> 'shards') with ordinality as s(shard, ordinal)
             where m.id = '{manifestId}'
             order by s.ordinal
            """);

    /// <summary>The run's planned executions as <c>shard-&lt;id&gt;:&lt;count&gt;</c>, ordered by shard id.</summary>
    private Task<IReadOnlyList<string>> PlannedPerShardAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"select shard_id || ':' || count(*)::text from public.planned_execution where run_id = '{runId}' group by shard_id order by shard_id");

    /// <summary>Every state the probe trigger has recorded, in insertion order.</summary>
    private Task<IReadOnlyList<string>> ProbeStatesAsync(RunCreationTests.World world) =>
        _fixture.ColumnAsync<string>(world.Database, "select state from public.run_state_probe order by at, state");

    /// <summary>
    /// A side table plus an AFTER INSERT OR UPDATE trigger on <c>public.run</c> that records every state the row takes.
    /// The app role gets INSERT on the probe table and EXECUTE on the function, so the trigger runs under the create
    /// transaction's role and RLS on <c>run</c> is irrelevant to it.
    /// </summary>
    private Task InstallStateProbeAsync(string database) => _fixture.ExecuteAsync(
        database,
        """
        create table public.run_state_probe (
          state text not null,
          at timestamptz not null default clock_timestamp()
        );
        create function public.probe_run_state() returns trigger language plpgsql as $$
        begin
          insert into public.run_state_probe (state) values (new.state);
          return null;
        end
        $$;
        create trigger run_state_probe after insert or update on public.run
          for each row execute function public.probe_run_state();
        grant insert on public.run_state_probe to evidgate_app;
        grant execute on function public.probe_run_state() to evidgate_app;
        """);

    /// <summary>The state literals named by a CHECK definition such as <c>state = ANY (ARRAY['created'::text, ...])</c>.</summary>
    private static IReadOnlyList<string> StateLiterals(string definition) =>
        [.. Regex.Matches(definition, @"'([a-z_]+)'(?:::text)?").Select(match => match.Groups[1].Value)];

    /// <summary>One <c>public.run</c> row in <paramref name="state"/> on the caller's transaction (rolled back by the test).</summary>
    private static async Task InsertRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RunCreationTests.World world,
        string state)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into public.run
                   (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, state,
                    collection_deadline_at, late_window_minutes)
            values (@id, @org_id, @project_id, @manifest_id, @suite_revision_id, @environment_revision_id, @state,
                    now() + interval '2 hours', 1440)
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("project_id", world.Project);
        command.Parameters.AddWithValue("manifest_id", world.ManifestId);
        command.Parameters.AddWithValue("suite_revision_id", world.SuiteRevision);
        command.Parameters.AddWithValue("environment_revision_id", world.EnvironmentRevision);
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
