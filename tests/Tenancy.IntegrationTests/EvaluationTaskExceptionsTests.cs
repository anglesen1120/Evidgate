using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationTaskTests
{
    /// <summary>
    /// A quarantine of a non-critical failing case stores PASS with the case id and <c>quarantined_case_failed</c>; the
    /// same entry on a critical case still fails; a case-scope waiver annotates a FAIL and a suite-scope one does not.
    /// The second evaluation reads the winner back through the stored row.
    /// </summary>
    [Theory]
    [InlineData("quarantine", false, "PASS")]
    [InlineData("quarantine", true, "FAIL")]
    [InlineData("case", false, "FAIL")]
    [InlineData("suite", false, "FAIL")]
    public async Task Evaluate_TargetedException_StoresItsEffectAndReadsBackTheWinner(
        string exceptionKind, bool critical, string decision)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var matrix = MatrixRow("M1") with
        {
            Body = cases => critical ? $$"""{"critical_cases":["{{cases.FirstId}}"]}""" : "{}",
            FirstCaseStatus = "failed",
        };
        var seed = await SeedMatrixAsync(world, matrix);
        var caseId = (await FirstPlannedCaseAsync(world, seed.RunId)).FirstId;
        if (exceptionKind == "quarantine")
        {
            await InsertQuarantineAsync(world, seed, "active", "now() + interval '1 hour'", caseId);
        }
        else
        {
            await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'", target: caseId, scope: exceptionKind);
        }

        var token = TestContext.Current.CancellationToken;
        var inserted = await EvaluateAsync(world, seed, EvaluationMode.Live, token);
        var stored = await ReloadAsync(world, seed, "live");
        var winner = await EvaluateAsync(world, seed, EvaluationMode.Live, token);

        stored.Completeness.ShouldBe("complete");
        stored.Missing.ShouldBeEmpty();
        stored.Outcome.ShouldBe("has_failures");
        stored.Decision.ShouldBe(decision);
        stored.Reasons.Contains("required_assertion_failed").ShouldBe(decision == "FAIL");
        stored.Reasons.Contains("waiver_approved").ShouldBe(exceptionKind == "case");
        stored.Reasons.Contains("quarantined_case_failed").ShouldBe(decision == "PASS");
        Guid[] expectedCases = decision == "PASS" ? [caseId] : [];
        stored.QuarantinedCases.ShouldBe(expectedCases);
        DecidingOf(inserted).ShouldBe(stored.Deciding);
        winner.Id.ShouldBe(inserted.Id);
        DecidingOf(winner).ShouldBe(stored.Deciding);
    }

    /// <summary>
    /// A quarantine and a later-expiring case waiver hold on one failed required case. One microsecond before the
    /// quarantine expires the case is excluded and the verdict passes; at the expiry instant the entry is out of the
    /// snapshot, the failure gates again and the waiver annotates it.
    /// </summary>
    [Fact]
    public async Task Evaluate_TargetedQuarantineAtExpiry_RevealsWaivedFailure()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedMatrixAsync(world, MatrixRow("M1") with { FirstCaseStatus = "failed" });
        var caseId = (await FirstPlannedCaseAsync(world, seed.RunId)).FirstId;
        const string expirySql = "timestamptz '2030-01-01 00:00:00+00'";
        var expiry = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var quarantine = await InsertQuarantineAsync(world, seed, "active", expirySql, caseId);
        var waiver = await InsertWaiverAsync(world, seed, "approved", expirySql + " + interval '1 hour'", target: caseId);
        var token = TestContext.Current.CancellationToken;
        await using var scope = await OpenScopeAsync(world, seed, token);
        await using var services = world.Api.Services.CreateAsyncScope();

        // Separate storage slots prevent the second evaluation from reading back the first verdict.
        var before = (await HandlerWithClock(services.ServiceProvider, new CountingClock(expiry.AddTicks(-10)))
            .Handle(new EvaluateRunCommand(seed.RunId, 1, EvaluationMode.Live), token)).Value;
        var atExpiry = (await HandlerWithClock(services.ServiceProvider, new CountingClock(expiry))
            .Handle(new EvaluateRunCommand(seed.RunId, 1, EvaluationMode.Shadow), token)).Value;

        before.Decision.ShouldBe("PASS");
        before.QuarantinedCases.ShouldBe([caseId]);
        before.Reasons.ShouldContain("quarantined_case_failed");
        before.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf([waiver], [quarantine]));
        atExpiry.Decision.ShouldBe("FAIL");
        atExpiry.Outcome.ShouldBe("has_failures");
        atExpiry.QuarantinedCases.ShouldBeEmpty();
        atExpiry.Reasons.ShouldBe(["required_assertion_failed", "waiver_approved"]);
        atExpiry.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf([waiver], []));
        atExpiry.ExceptionsHash.ShouldNotBe(before.ExceptionsHash);
    }

    /// <summary>
    /// Only an approved, unexpired waiver counts: requested, revoked and past-expiry waivers are left out of the snapshot,
    /// so the stored hash is the digest of exactly the one live id.
    /// </summary>
    [Fact]
    public async Task Evaluate_OnlyApprovedUnexpiredWaiversAreInTheSnapshot()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var active = await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'");
        await InsertWaiverAsync(world, seed, "approved", "now() - interval '1 hour'");
        await InsertWaiverAsync(world, seed, "requested", "now() + interval '1 hour'");
        await InsertWaiverAsync(world, seed, "revoked", "now() + interval '1 hour'");
        await InsertQuarantineAsync(world, seed, "removed", "now() + interval '1 hour'");
        await InsertQuarantineAsync(world, seed, "active", "now() - interval '1 hour'");

        var record = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        record.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf([active], []));
    }

    /// <summary>
    /// A different active set gives a different hash, and the same set read by two invocations gives the same one,
    /// whatever order the ids were written in.
    /// </summary>
    [Fact]
    public async Task Evaluate_DifferentWaiverSetsDifferInHashAndIdenticalSetsMatch()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var cancellationToken = TestContext.Current.CancellationToken;

        var before = await EvaluateAsync(world, seed, EvaluationMode.Live, cancellationToken);
        var first = await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'");
        var second = await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'");
        var quarantine = await InsertQuarantineAsync(world, seed, "active", "now() + interval '1 hour'");

        var shadow = await EvaluateAsync(world, seed, EvaluationMode.Shadow, cancellationToken);

        shadow.ExceptionsHash.ShouldNotBe(before.ExceptionsHash, "a larger active set is a different snapshot");
        shadow.ExceptionsHash.ShouldBe(
            ExceptionsSnapshotResolver.HashOf([.. new[] { first, second }.OrderBy(id => id.ToString("D"))], [quarantine]));
        before.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf([], []));

        // The same set, read by a fresh scope, hashes the same: the digest depends on the ids and not the read.
        await using var scope = await OpenScopeAsync(world, seed, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var resolver = services.ServiceProvider.GetRequiredService<ExceptionsSnapshotResolver>();
        var again = await resolver.ResolveAsync(seed.Project, shadow.ExceptionsAsOf, cancellationToken);
        again.Hash.ShouldBe(shadow.ExceptionsHash);
    }

    /// <summary>
    /// The snapshot is a function of the instant it is handed: a waiver in force before its expiry is in the set, and the
    /// same query one second later is not. Nothing inside the query reads the clock.
    /// </summary>
    [Fact]
    public async Task ExceptionsSnapshot_FollowsTheCapturedInstantNotTheClock()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var cancellationToken = TestContext.Current.CancellationToken;
        var waiver = await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'");
        var expiresAt = await WaiverExpiryAsync(world, waiver);

        await using var scope = await OpenScopeAsync(world, seed, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var resolver = services.ServiceProvider.GetRequiredService<ExceptionsSnapshotResolver>();

        var inside = await resolver.ResolveAsync(seed.Project, expiresAt.AddSeconds(-1), cancellationToken);
        var outside = await resolver.ResolveAsync(seed.Project, expiresAt.AddSeconds(1), cancellationToken);

        inside.WaiverIds.ShouldBe([waiver]);
        outside.WaiverIds.ShouldBeEmpty();
        inside.Hash.ShouldNotBe(outside.Hash);
        inside.AsOf.ShouldBe(expiresAt.AddSeconds(-1));
    }

    /// <summary>The control for the replay row: the same run as a regular run stores the live row it was asked for.</summary>
    [Fact]
    public async Task Evaluate_RegularRun_StillStoresLiveRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);

        var record = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        record.Mode.ShouldBe("live");
    }

    /// <summary>A waiver of another project of the same org is not part of this run's snapshot.</summary>
    [Fact]
    public async Task Evaluate_WaiverOfAnotherProject_IsNotInTheSnapshot()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var other = await InsertProjectAsync(world, seed);
        await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'", other);
        var own = await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'");

        var record = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        record.ExceptionsHash.ShouldBe(ExceptionsSnapshotResolver.HashOf([own], []));
    }

    /// <summary>Two persisted evaluations of one unchanged exception set store the same hash.</summary>
    [Fact]
    public async Task Evaluate_IdenticalSetsOnTwoInvocations_StoreTheSameHash()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var cancellationToken = TestContext.Current.CancellationToken;
        await InsertWaiverAsync(world, seed, "approved", "now() + interval '1 hour'");
        await InsertQuarantineAsync(world, seed, "active", "now() + interval '1 hour'");

        var live = await EvaluateAsync(world, seed, EvaluationMode.Live, cancellationToken);
        var shadow = await EvaluateAsync(world, seed, EvaluationMode.Shadow, cancellationToken);

        live.Id.ShouldNotBe(shadow.Id);
        shadow.ExceptionsHash.ShouldBe(live.ExceptionsHash);
    }

    /// <summary>The handler reads the clock once per attempt, and the instant it reads is the one the row stores.</summary>
    [Fact]
    public async Task Evaluate_ReadsTheClockOnceAndStoresThatInstant()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new CountingClock(DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var scope = await OpenScopeAsync(world, seed, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var handler = HandlerWithClock(services.ServiceProvider, clock);

        var result = await handler.Handle(new EvaluateRunCommand(seed.RunId, 1, EvaluationMode.Live), cancellationToken);

        clock.Reads.ShouldBe(1);
        result.Value.ExceptionsAsOf.ShouldBe(clock.Instant);
    }

    private static EvaluateRunCommandHandler HandlerWithClock(IServiceProvider provider, TimeProvider clock) =>
        new(
            provider.GetRequiredService<EvaluationInputReader>(),
            provider.GetRequiredService<ExceptionsSnapshotResolver>(),
            provider.GetRequiredService<Evidgate.Evaluation.IPolicyEvaluator>(),
            provider.GetRequiredService<ITenantDb>(),
            provider.GetRequiredService<ITenantScopeAccessor>(),
            clock);

    private sealed class CountingClock(DateTimeOffset instant) : TimeProvider
    {
        public int Reads { get; private set; }

        public DateTimeOffset Instant { get; } = instant;

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return Instant;
        }
    }

    private async Task<Guid> InsertProjectAsync(RunCreationTests.World world, Seed seed)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, 'other', 'other-project')";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("org_id", seed.Org);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> InsertWaiverAsync(
        RunCreationTests.World world, Seed seed, string state, string expiresAtSql,
        Guid? projectId = null, Guid? target = null, string scope = "case")
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            values (@id, @org_id, @project_id, @scope, @target, 'known issue', @requested_by,
                    case when @state = 'approved' then @approver end, @state, {expiresAtSql})
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("project_id", projectId ?? seed.Project);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("target", target ?? Guid.CreateVersion7());
        command.Parameters.AddWithValue("requested_by", Guid.CreateVersion7());
        command.Parameters.AddWithValue("approver", Guid.CreateVersion7());
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> InsertQuarantineAsync(
        RunCreationTests.World world, Seed seed, string state, string expiresAtSql, Guid? caseId = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at)
            select @id, @org_id, @project_id, coalesce(@case_id, c.id), m.id, 'flaky', @state, {expiresAtSql}
              from (select id from public.test_case where org_id = @org_id limit 1) c,
                   (select id from public.membership where org_id = @org_id limit 1) m
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("project_id", seed.Project);
        // Existing callers quarantine any catalog case; the targeted ones name the failing case.
        command.Parameters.Add(new Npgsql.NpgsqlParameter("case_id", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = (object?)caseId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("state", state);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken))
            .ShouldBe(1, "the world must hold a catalog case and a membership to quarantine against");
        return id;
    }

    private async Task<DateTimeOffset> WaiverExpiryAsync(RunCreationTests.World world, Guid waiverId)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText = "select expires_at from public.waiver where id = @id";
        command.Parameters.AddWithValue("id", waiverId);
        return new DateTimeOffset(
            DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!, DateTimeKind.Utc));
    }

    private async Task MarkReplayAsync(RunCreationTests.World world, Seed seed)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update public.run set purpose = 'replay', replay_grant_id = @grant
             where org_id = @org_id and id = @run_id
            """;
        command.Parameters.AddWithValue("grant", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("run_id", seed.RunId);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }
}
