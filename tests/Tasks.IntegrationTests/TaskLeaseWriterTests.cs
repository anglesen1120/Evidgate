using Evidgate.BuildingBlocks.Persistence.Tasks;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1 §6, the lease token: a transition only applies while the row still carries
/// the claim that produced it. The guard is the claim's `id` + `state='active'` +
/// `locked_until` + `version`, and the version term is what makes it a real token —
/// `version` is bumped by the shared trigger on every update of the row, so a stale
/// attempt cannot complete, retry or dead-letter the newer attempt's row, whatever
/// its lease timestamp happens to be. That covers the ABA shape (a later claim whose
/// `locked_until` equals the stale one) and any other intervening write, an operator
/// replay included.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskLeaseWriterTests
{
    private const string Queue = "lease";

    private readonly PostgresTasksFixture _fixture;

    public TaskLeaseWriterTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stale_Claim_Cannot_Apply_Its_Outcome()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "lease-token", maxAttempts: 5);

        await using var host = TasksTestHost.Start(_fixture, database);

        var stale = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();
        stale.Attempts.ShouldBe(1);

        // The slow worker's lease runs out; another consumer reclaims the row and takes
        // it as attempt 2. `version` now differs from the stale claim's on both counts.
        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set locked_until = now() - interval '1 second' where id = @id",
            ("id", stale.Id));

        var newer = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();
        newer.Id.ShouldBe(stale.Id);
        newer.Attempts.ShouldBe(2);
        newer.Version.ShouldBeGreaterThan(stale.Version);

        // Every transition of the superseded attempt is refused…
        (await host.Leases.MarkDoneAsync(stale, Token))
            .ShouldBeFalse("a superseded claim must not complete the row");
        (await host.Leases.MarkRetryAsync(stale, TimeSpan.FromHours(1), "stale", Token))
            .ShouldBeFalse("a superseded claim must not retry the row");
        (await host.Leases.MarkDeadLetterAsync(stale, "stale", Token))
            .ShouldBeFalse("a superseded claim must not dead-letter the row");

        // …and the row is untouched: the newer attempt still owns it.
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where id = @id", ("id", stale.Id)))
            .ShouldBe("active");
        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where id = @id", ("id", stale.Id)))
            .ShouldBeNull();

        // The current claim can finish it.
        (await host.Leases.MarkDoneAsync(newer, Token)).ShouldBeTrue();
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where id = @id", ("id", stale.Id)))
            .ShouldBe("done");
    }

    /// <summary>
    /// 01.5.1-T1.3: releasing a claim that never ran gives the attempt back — the cap
    /// counts handler attempts, so a row that only waited in a slow batch must not
    /// arrive at `dead_letter` with its retries spent.
    /// </summary>
    [Fact]
    public async Task Release_Gives_The_Attempt_Back()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "release");

        await using var host = TasksTestHost.Start(_fixture, database);

        var claim = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();
        claim.Attempts.ShouldBe(1);

        (await host.Leases.ReleaseAsync(claim, Token)).ShouldBeTrue();

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where id = @id", ("id", claim.Id)))
            .ShouldBe("queued");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where id = @id", ("id", claim.Id)))
            .ShouldBe(0, "the attempt of a claim that never ran is given back");
        (await TaskSql.ScalarAsync<bool>(owner, "select locked_until is null from public.task where id = @id", ("id", claim.Id)))
            .ShouldBeTrue();

        // The released claim is dead: a second release with the same token changes nothing.
        (await host.Leases.ReleaseAsync(claim, Token)).ShouldBeFalse();
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where id = @id", ("id", claim.Id)))
            .ShouldBe(0, "a superseded release must not drive attempts below zero");
    }

    /// <summary>
    /// The claim carries the token, so a consumer cannot lose it half-way: the row's
    /// `version` as the claim returned it is the post-update value (the shared trigger
    /// bumps it inside the claim's own UPDATE).
    /// </summary>
    [Fact]
    public async Task Claim_Returns_The_Version_Its_Own_Update_Produced()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "lease-version");

        await using var host = TasksTestHost.Start(_fixture, database);

        var claim = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();

        // A fresh row starts at version 1 and the claim's UPDATE is its first write.
        claim.Version.ShouldBe(2);
        (await TaskSql.ScalarAsync<int>(owner, "select version from public.task where id = @id", ("id", claim.Id)))
            .ShouldBe(2);
    }
}
