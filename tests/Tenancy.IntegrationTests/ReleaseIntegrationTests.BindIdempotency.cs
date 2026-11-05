// The idempotency and concurrency half of the explicit bind: one key never writes twice, a different key never
// duplicates a row, and two simultaneous binds of one pair both succeed with one row per component key.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>How many idempotency records the bind key claimed for the world's org.</summary>
    private async Task<long> BindKeyCountAsync(RunCreationTests.World world, string key) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org:D}' "
            + $"and operation = 'release:bind' and key = '{key}'").ConfigureAwait(false);

    /// <summary>The <c>release.bind</c> audit rows of one release.</summary>
    private async Task<long> BindAuditCountAsync(RunCreationTests.World world, Guid releaseId) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' "
            + $"and action = 'release.bind' and object_id = '{releaseId:D}'").ConfigureAwait(false);

    /// <summary>The <c>release.bound</c> outbox rows of the world's org.</summary>
    private async Task<long> BoundEventCountAsync(RunCreationTests.World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = 'orchestrator.release.bound'")
            .ConfigureAwait(false);

    [Fact]
    public async Task IdempotentBind_SameKeyReplaysTheOriginalRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();
        var releaseId = await ReleaseWithAsync(world, "bind-replay", "active", ("web", 'a'), ("api", 'b'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b')));

        var first = await BindAsync(world, releaseId, runId, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var replay = await BindAsync(world, releaseId, runId, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(2);
        (await BindKeyCountAsync(world, key)).ShouldBe(1);
        (await BindAuditCountAsync(world, releaseId)).ShouldBe(1);
        (await BoundEventCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task BindReplayDifferentPayload_409IdempotencyConflict()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();
        var releaseId = await ReleaseWithAsync(world, "bind-conflict", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        var otherRun = await RunWithAsync(world, Components(("web", 'a')));

        (await BindAsync(world, releaseId, runId, idempotencyKey: key)).Status.ShouldBe(HttpStatusCode.Created);

        var conflicting = await BindAsync(world, releaseId, otherRun, idempotencyKey: key);

        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["web"]);
        (await BindingCountAsync(world, releaseId, otherRun)).ShouldBe(0);
        (await BindKeyCountAsync(world, key)).ShouldBe(1);
    }

    /// <summary>A different key for a pair whose every key is already bound writes nothing and answers the rows.</summary>
    [Fact]
    public async Task Rebind_DifferentKey_IsIdempotentAndWritesNoDuplicate()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-rebind", "active", ("web", 'a'), ("api", 'b'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b')));

        (await BindAsync(world, releaseId, runId)).Status.ShouldBe(HttpStatusCode.Created);
        var second = await BindAsync(world, releaseId, runId);

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        BoundOf(second).ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web"] = Digest('a'),
            ["api"] = Digest('b'),
        });
        MismatchedOf(second).ShouldBeEmpty();
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(2);
        (await BindAuditCountAsync(world, releaseId)).ShouldBe(1);
        (await BoundEventCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A later bind of the same pair that finds one more matching key inserts just that row: the already bound key is
    /// left alone by the unique tuple, and the event is enqueued once for the bind that changed something.
    /// </summary>
    [Fact]
    public async Task Rebind_AfterTheRunRecordsAnotherMatchingComponent_AddsOnlyThatKey()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-late-key", "active", ("web", 'a'), ("api", 'b'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        (await BindAsync(world, releaseId, runId)).Status.ShouldBe(HttpStatusCode.Created);
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["web"]);

        // The run gains the second component (a later manifest upload does exactly this through the run row).
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set components = '{{\"web\":\"{Digest('a')}\",\"api\":\"{Digest('b')}\"}}'::jsonb "
            + $"where id = '{runId:D}'");

        var second = await BindAsync(world, releaseId, runId);

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["api", "web"]);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(2);
        (await BoundEventCountAsync(world)).ShouldBe(2);
    }

    /// <summary>Two simultaneous binds of one pair: both succeed and the unique tuple keeps one row per key.</summary>
    [Fact]
    public async Task ConcurrentBinds_OfOnePair_BothSucceedWithOneRowPerKey()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-race", "active", ("web", 'a'), ("api", 'b'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b')));

        var answers = await Task.WhenAll(
            BindAsync(world, releaseId, runId),
            BindAsync(world, releaseId, runId));

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
            BoundOf(answer).Keys.ShouldBe(["api", "web"], ignoreOrder: true);
        }

        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}' and release_id = '{releaseId:D}' "
            + $"and run_id = '{runId:D}' and component_key = 'web'")).ShouldBe(1);
    }
}
