// QA suite for 05.2.1-T2 (run-create idempotency): boundary, negative and exploratory cases the implementer's suite does
// not cover. Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>QA cases for the run-create <c>Idempotency-Key</c> (05.2.1-T2 AC3/AC4) through the real host and PostgreSQL.</summary>
[Trait("Category", "Docker")]
public sealed class RunCreationIdempotencyQaTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    // Reuse one API host per class; each world still owns its tenant data.
    // Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts;

    public RunCreationIdempotencyQaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    private Task<long> RecordCountAsync(RunCreationTests.World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where operation = 'run:create' and org_id = '{world.Org:D}'");

    private Task<long> RunCountAsync(RunCreationTests.World world) =>
        _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.run where project_id = '{world.Project:D}'");

    [Fact]
    public async Task Key_WhitespaceOnly_Is400_AndWritesNoRecord()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: "   ");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        (await RecordCountAsync(world)).ShouldBe(0);
        (await RunCountAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------ rejected requests do not burn the key

    [Fact]
    public async Task InvalidJson_ThenValidBody_SameKey_Succeeds()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var broken = await RunCreationTests.CreateRunAsync(world, "{\"manifest_id\":", idempotencyKey: key);
        broken.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, broken.ErrorMessage);
        (await RecordCountAsync(world)).ShouldBe(0);

        var valid = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);
        valid.Status.ShouldBe(HttpStatusCode.Created, valid.ErrorMessage);
        (await RunCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task UnknownMember_ThenValidBody_SameKey_Succeeds()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var withExtra = $"{{\"manifest_id\":\"{world.ManifestId}\",\"bogus\":1}}";

        var refused = await RunCreationTests.CreateRunAsync(world, withExtra, idempotencyKey: key);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        (await RecordCountAsync(world)).ShouldBe(0);

        var valid = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);
        valid.Status.ShouldBe(HttpStatusCode.Created, valid.ErrorMessage);
    }

    [Fact]
    public async Task DuplicateMemberNames_WithKey_ReplayIsStableOrRefused()
    {
        // Duplicate member names make the canonical JSON fail, so the request hash falls back to raw-sha256. Whatever the
        // handler decides (accept with last-wins or refuse), the same key and the same raw bytes must answer the same way.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = $"{{\"manifest_id\":\"{world.ManifestId}\",\"manifest_id\":\"{world.ManifestId}\"}}";

        var first = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);
        var second = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        second.Status.ShouldBe(first.Status);
        if (first.Status == HttpStatusCode.Created)
        {
            second.Body.GetProperty("run_id").GetGuid().ShouldBe(first.Body.GetProperty("run_id").GetGuid());
            (await RunCountAsync(world)).ShouldBe(1);
        }
    }

    // ------------------------------------------------------------ authorization runs before replay

    [Fact]
    public async Task CallerWithoutRunCreate_WithKey_Is403_AndBurnsNoKey()
    {
        // Authorization sits before Idempotency in the pipeline: an unauthorized caller must not claim or replay the key.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var noPermission = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: ["none.none"]);

        var denied = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), caller: noPermission, idempotencyKey: key);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden, denied.ErrorMessage);
        (await RecordCountAsync(world)).ShouldBe(0);

        var allowed = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);
        allowed.Status.ShouldBe(HttpStatusCode.Created, allowed.ErrorMessage);
    }

    [Fact]
    public async Task AnonymousCaller_WithKey_Is401_AndWritesNoRecord()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            caller: _ => { },
            idempotencyKey: NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized, answer.ErrorMessage);
        (await RecordCountAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------ replay after conflict and retention

    [Fact]
    public async Task Conflict_DoesNotOverwriteTheStoredResponse()
    {
        // A 409 on a mismatched body must leave the original response in place: a later replay still returns the first run.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var original = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);
        original.Status.ShouldBe(HttpStatusCode.Created, original.ErrorMessage);

        var conflict = await RunCreationTests.CreateRunAsync(world, $"{{\"manifest_id\":\"{world.ManifestId}\",\"release\":\"other\"}}", idempotencyKey: key);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict, conflict.ErrorMessage);

        var replay = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);
        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Body.GetProperty("run_id").GetGuid().ShouldBe(original.Body.GetProperty("run_id").GetGuid());
        (await RunCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task RecordRetention_IsAtLeastTwentyFourHours()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        var seconds = await _fixture.ScalarAsync<long>(
            world.Database,
            "select extract(epoch from (expires_at - created_at))::bigint from public.idempotency_record "
            + $"where operation = 'run:create' and org_id = '{world.Org:D}'");

        seconds.ShouldBeGreaterThanOrEqualTo(86_400L);
    }
}
