using Evidgate.Modules.Orchestrator.Domain;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The run state guard and the producer-slot guard (migration 0099) against a real migrated database: the app role
/// moves every edge of <see cref="RunStateMachine.DeclaredEdges"/>, is refused with SQLSTATE 23514 on every other pair,
/// and a refused update leaves the row (state and version) untouched.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunSlotGuardTests(PostgresTenancyFixture fixture)
{
    private const string UpdateState = "update public.run set state = @state where id = @run_id";

    private readonly PostgresTenancyFixture _fixture = fixture;

    public static TheoryData<string, string> AllowedEdges => Pairs((from, to) => RunStateMachine.DeclaredEdges.Contains((from, to)));

    public static TheoryData<string, string> IllegalEdges =>
        Pairs((from, to) => from != to && !RunStateMachine.DeclaredEdges.Contains((from, to)));

    public static TheoryData<string> SameStates => [.. RunStateMachine.AllStates];

    private sealed record Seeded(string Database, Guid Org, Guid RunId);

    [Fact]
    public void Matrix_CoversEveryNonSelfPair()
    {
        var total = RunStateMachine.AllStates.Count * (RunStateMachine.AllStates.Count - 1);

        (AllowedEdges.Count + IllegalEdges.Count).ShouldBe(total);
        AllowedEdges.Count.ShouldBe(11);
    }

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public async Task RunState_AllowedEdge_Succeeds(string from, string to)
    {
        var seeded = await SeedRunAtAsync(from);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            (await scope.ExecuteAsync(UpdateState, ("state", to), ("run_id", seeded.RunId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await ScalarAsync<string>(seeded, "state")).ShouldBe(to);
    }

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public async Task RunState_IllegalEdge_RefusedAndRowUnchanged(string from, string to)
    {
        var seeded = await SeedRunAtAsync(from);
        var version = await ScalarAsync<int>(seeded, "version");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            var error = await scope.CaptureErrorAsync(UpdateState, ("state", to), ("run_id", seeded.RunId));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.MessageText.ShouldBe($"run_illegal_transition: {from} -> {to}");
        }

        (await ScalarAsync<string>(seeded, "state")).ShouldBe(from);
        (await ScalarAsync<int>(seeded, "version")).ShouldBe(version);
    }

    [Theory]
    [MemberData(nameof(SameStates))]
    public async Task RunState_SameState_NoOp_Succeeds(string state)
    {
        var seeded = await SeedRunAtAsync(state);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            (await scope.ExecuteAsync(UpdateState, ("state", state), ("run_id", seeded.RunId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await ScalarAsync<string>(seeded, "state")).ShouldBe(state);
    }

    [Fact]
    public async Task RunState_CompleteToRunning_RefusedForTheOwnerRoleToo()
    {
        var seeded = await SeedRunAtAsync(RunStates.Complete);

        var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(
            seeded.Database, $"update public.run set state = 'running' where id = '{seeded.RunId:D}'"));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.MessageText.ShouldBe("run_illegal_transition: complete -> running");
    }

    [Fact]
    public async Task Slot_ClosedAtCleared_Refused()
    {
        var seeded = await SeedSlotAsync(closed: true);

        var error = await CaptureSlotErrorAsync(seeded, "closed_at = null");

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.MessageText.ShouldStartWith("producer_slot_slot_reopened");
    }

    [Fact]
    public async Task Slot_ClosedAtChanged_Refused()
    {
        var seeded = await SeedSlotAsync(closed: true);

        var error = await CaptureSlotErrorAsync(seeded, "closed_at = closed_at + interval '1 second'");

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.MessageText.ShouldStartWith("producer_slot_slot_reopened");
    }

    [Fact]
    public async Task Slot_SequenceRegress_Refused_OpenAndClosed()
    {
        foreach (var closed in new[] { false, true })
        {
            var seeded = await SeedSlotAsync(closed);
            await ExecuteSlotAsync(seeded, "last_sequence = 9");

            var error = await CaptureSlotErrorAsync(seeded, "last_sequence = 8");

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.MessageText.ShouldBe("producer_slot_sequence_regressed: 9 -> 8");
            (await SlotSequenceAsync(seeded)).ShouldBe(9);
        }
    }

    [Fact]
    public async Task Slot_LegalClose_ForwardAndEqualSequence_Succeed()
    {
        var seeded = await SeedSlotAsync(closed: false);

        await ExecuteSlotAsync(seeded, "last_sequence = 5");
        await ExecuteSlotAsync(seeded, "last_sequence = 5");
        await ExecuteSlotAsync(seeded, "closed_at = now(), declared_last_sequence = 5, declared_count = 5");
        await ExecuteSlotAsync(seeded, "last_sequence = 6");

        (await SlotSequenceAsync(seeded)).ShouldBe(6);
    }

    private static TheoryData<string, string> Pairs(Func<string, string, bool> keep)
    {
        var data = new TheoryData<string, string>();
        foreach (var from in RunStateMachine.AllStates)
        {
            foreach (var to in RunStateMachine.AllStates)
            {
                if (keep(from, to))
                {
                    data.Add(from, to);
                }
            }
        }

        return data;
    }

    private async Task<Seeded> SeedRunAtAsync(string state)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database);
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);
        await RunStateWalk.MoveAsync(_fixture, database, runId, state);
        return new Seeded(database, org, runId);
    }

    private async Task<Seeded> SeedSlotAsync(bool closed)
    {
        var seeded = await SeedRunAtAsync(RunStates.Queued);
        await _fixture.ExecuteAsync(
            seeded.Database,
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id) "
            + $"values ('{Guid.CreateVersion7():D}', '{seeded.Org:D}', '{seeded.RunId:D}', 1, 'shard-1')");
        if (closed)
        {
            await ExecuteSlotAsync(seeded, "closed_at = now(), declared_last_sequence = 0, declared_count = 0");
        }

        return seeded;
    }

    private async Task<Guid> SeedOrgAsync(string database)
    {
        var org = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.organization (id, name, slug) values ('{org:D}', 'run guard', '{Unique.Slug("org")}')");
        await _fixture.ExecuteAsync(
            database,
            "insert into public.project (id, org_id, name, slug) "
            + $"values ('{Guid.CreateVersion7():D}', '{org:D}', 'run guard', '{Unique.Slug("project")}')");
        return org;
    }

    private Task ExecuteSlotAsync(Seeded seeded, string assignment) =>
        _fixture.ExecuteAsync(seeded.Database, $"update public.producer_slot set {assignment} where run_id = '{seeded.RunId:D}'");

    private async Task<PostgresException> CaptureSlotErrorAsync(Seeded seeded, string assignment)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(seeded.Database), seeded.Org);
        return await scope.CaptureErrorAsync(
            $"update public.producer_slot set {assignment} where run_id = '{seeded.RunId:D}'");
    }

    private Task<long> SlotSequenceAsync(Seeded seeded) =>
        _fixture.ScalarAsync<long>(seeded.Database, $"select last_sequence from public.producer_slot where run_id = '{seeded.RunId:D}'");

    private Task<T> ScalarAsync<T>(Seeded seeded, string column) =>
        _fixture.ScalarAsync<T>(seeded.Database, $"select {column} from public.run where id = '{seeded.RunId:D}'");
}
