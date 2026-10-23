// 04.1.2-T5 review fix (Code P2, T88): the run's CI reference (ci_run_ref jsonb) is part of the
// producer slot's identity, and the batch handler must decide the slot bind BEFORE it answers
// unknown execution keys, so a foreign or mismatching token always learns 403 producer_slot_taken /
// 422 unknown_producer and never which execution keys the run plans. Written against the task file's
// contract (T88/T9/T10, the shared "run scope -> bind -> unknown key" ordering), not the handler
// internals.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The run-level CI reference (T88) as part of the slot identity, and the batch handler's ordering of
/// the slot bind against the unknown-execution-key answer (T9/T10): a foreign token must never learn
/// which execution keys the run plans.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class BindingCiRunRefTests
{
    private readonly PostgresTenancyFixture _fixture;

    public BindingCiRunRefTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The run's CI reference: repository_id/run_id/run_attempt = 1/7/2 (the 0029/0031 test value).</summary>
    private const string RunCiRunRefJson =
        """{"provider":"github_actions","repository_id":1,"run_id":7,"run_attempt":2}""";

    /// <summary>The caller reference that matches the run's, <c>{repository_id}/{run_id}/{run_attempt}</c>.</summary>
    private const string MatchingRef = "1/7/2";

    /// <summary>A foreign reference: another attempt of the same CI run is a different identity (T9/T88).</summary>
    private const string ForeignRef = "1/7/3";

    private static readonly CiRunReference MatchingCiRun = new("github_actions", 1L, 7L, 2);

    private static readonly CiRunReference ForeignCiRun = new("github_actions", 1L, 7L, 3);

    /// <summary>A well-formed execution key (sha256 hex) that no seeded plan contains.</summary>
    private static readonly string UnplannedKey = new('f', 64);

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid Org) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and a project created through the real routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, org);
    }

    /// <summary>Runs one registry call in a fresh writable ambient tenant scope and commits it.</summary>
    private static async Task<T> InScopeAsync<T>(World world, Func<IProducerRegistry, ValueTask<T>> call)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var result = await call(services.ServiceProvider.GetRequiredService<IProducerRegistry>());
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return result;
    }

    /// <summary>
    /// One committed run row for <paramref name="world"/>'s org, with its <c>ci_run_ref</c> (T88) when
    /// given. The reference is written as the migration role: the app role has no UPDATE on <c>run</c>.
    /// </summary>
    private async Task<Guid> CreateRunAsync(World world, string? ciRunRef = null)
    {
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
        if (ciRunRef is not null)
        {
            await _fixture.ExecuteAsync(
                world.Database, $"update public.run set ci_run_ref = '{ciRunRef}'::jsonb where id = '{runId:D}'");
        }

        return runId;
    }

    /// <summary>Sets the ingest world's run <c>ci_run_ref</c> (T88) as the migration role.</summary>
    private static Task SetRunCiRunRefAsync(IngestTestWorld world, string json) =>
        world.Fixture.ExecuteAsync(
            world.Database, $"update public.run set ci_run_ref = '{json}'::jsonb where id = '{world.RunId:D}'");

    /// <summary>A service principal with <c>event:write</c> and the given token id and CI claims (T88).</summary>
    private static RequestPrincipal EventWriter(string jti, CiRunReference? ciRun) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Event.Write], credentialId: jti, ciRun: ciRun);

    // ---------------------------------------------------------------- T88: run.ci_run_ref is part of the identity

    /// <summary>
    /// T88: the run carries <c>1/7/2</c> and an unbound slot exists; a caller whose claims carry the
    /// foreign <c>1/7/3</c> is refused 403 <c>producer_slot_taken</c> and the slot stays unbound (the
    /// refusal rolls the bind back - here nothing was written at all).
    /// </summary>
    [Fact]
    public async Task ci_run_ref_mismatch_403_on_unbound_bind_leaves_slot_unbound()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world, RunCiRunRefJson);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r =>
            r.BindOrCheckAsync(run, "shard-1", "jti-a", ForeignRef, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        result.Error.Status.ShouldBe(403);

        var slots = await InScopeAsync(world, r => r.GetRunProducerSlotsAsync(run, TestContext.Current.CancellationToken));
        slots.Single().Bound.ShouldBeFalse("a refused bind must not leave the slot bound");

        await using var direct = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await direct.ScalarAsync<long>(
            "select count(*) from public.producer_slot where run_id = @run and bound_token_jti is not null",
            ("run", run))).ShouldBe(0);
    }

    /// <summary>T88: a run with a CI reference and a non-CI caller (null claims) is equally a mismatch: 403.</summary>
    [Fact]
    public async Task ci_run_ref_mismatch_403_when_caller_has_no_ci_reference()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world, RunCiRunRefJson);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r =>
            r.BindOrCheckAsync(run, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        result.Error.Status.ShouldBe(403);
    }

    /// <summary>
    /// T88/T9: only the bound identity may finalize a slot. With the run at <c>1/7/2</c> and the slot bound
    /// to <c>1/7/2</c>, a close claiming the foreign <c>1/7/3</c> is 403 <c>producer_slot_taken</c>.
    /// </summary>
    [Fact]
    public async Task ci_run_ref_mismatch_403_via_close_slot()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world, RunCiRunRefJson);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));
        var bound = await InScopeAsync(world, r =>
            r.BindOrCheckAsync(run, "shard-1", "jti-a", MatchingRef, TestContext.Current.CancellationToken));
        bound.IsSuccess.ShouldBeTrue(bound.Error?.Message);

        var closed = await InScopeAsync(world, r =>
            r.CloseSlotAsync(run, "shard-1", "jti-a", ForeignRef, 3, 3, TestContext.Current.CancellationToken));

        closed.IsSuccess.ShouldBeFalse();
        closed.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        closed.Error.Status.ShouldBe(403);
    }

    /// <summary>
    /// T88 over the real route: a run created from <c>1/7/2</c>, a foreign token minted for <c>1/7/3</c>
    /// posting a planned key is refused 403 <c>producer_slot_taken</c> and writes zero events.
    /// </summary>
    [Fact]
    public async Task ci_run_ref_mismatch_403_over_http_writes_no_events()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, EventWriter("jti-ci-foreign", ForeignCiRun), planned: 2, slots: 1);
        await SetRunCiRunRefAsync(world, RunCiRunRefJson);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("producer_slot_taken");
        (await world.CountAsync("event")).ShouldBe(0);
    }

    /// <summary>
    /// T88 over the real route: a token minted for the run's own CI reference (<c>1/7/2</c>) binds the slot
    /// and its planned event is accepted.
    /// </summary>
    [Fact]
    public async Task ci_run_ref_matching_binds_and_is_accepted_over_http()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, EventWriter("jti-ci-matching", MatchingCiRun), planned: 2, slots: 1);
        await SetRunCiRunRefAsync(world, RunCiRunRefJson);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.CountAsync("event")).ShouldBe(1);

        await using var direct = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await direct.ScalarAsync<string>(
            "select bound_ci_run_ref from public.producer_slot where run_id = @run and producer_id = 'shard-1'",
            ("run", world.RunId))).ShouldBe(MatchingRef);
    }

    /// <summary>
    /// T88: a run created without a CI reference has nothing to compare, so a CI caller still binds - the
    /// slot's own binding is then the whole identity.
    /// </summary>
    [Fact]
    public async Task ci_run_ref_absent_run_accepts_ci_caller()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r =>
            r.BindOrCheckAsync(run, "shard-1", "jti-a", ForeignRef, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Bound.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- T9/T10: bind before the unknown-key answer

    /// <summary>
    /// T9/T10, the shared ordering contract: a token that is foreign to the slot (bound to another
    /// identity) posting a batch whose execution key is unplanned is refused 403 <c>producer_slot_taken</c>,
    /// never 422 <c>unknown_execution_key</c> - a foreign token must not learn which keys the run plans.
    /// Zero events are written.
    /// </summary>
    [Fact]
    public async Task foreign_token_unplanned_key_403_taken_not_422_unknown_key()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 2, slots: 1, boundJti: IngestTestWorld.OtherJti);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), UnplannedKey, sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("producer_slot_taken");
        answer.ErrorCode.ShouldNotBe("unknown_execution_key");
        (await world.CountAsync("event")).ShouldBe(0);
    }

    /// <summary>
    /// T10: a correctly bound token posting unplanned keys still gets 422 <c>unknown_execution_key</c>,
    /// naming every failing index (here 1 and 2), and writes nothing.
    /// </summary>
    [Fact]
    public async Task bound_token_unplanned_keys_422_lists_all_indices()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 2, slots: 1, boundJti: IngestTestWorld.Jti);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1),
                IngestQaRequests.Event(Guid.CreateVersion7(), UnplannedKey, sequence: 2),
                IngestQaRequests.Event(Guid.CreateVersion7(), new string('e', 64), sequence: 3)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("unknown_execution_key");
        IngestQaRequests.Details(answer).Select(IngestQaRequests.IndexOf).ShouldBe([1, 2]);
        (await world.CountAsync("event")).ShouldBe(0);
    }
}
