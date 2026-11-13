// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T2 AC3/AC4 through the real host and a real PostgreSQL: the <c>Idempotency-Key</c> is claimed inside the run's
/// tenant transaction, so the <c>idempotency_record</c> commits or rolls back with the run. Same key + same canonical
/// body replays the original response and writes nothing; same key + a different canonical body is 409
/// <c>idempotency_conflict</c>; a failed attempt leaves no record, so the key is reusable.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunCreationIdempotencyTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    // Reuse one API host per class; each world still owns its tenant data.
    // Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts;

    public RunCreationIdempotencyTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    /// <summary>The four row counts a replay must leave untouched (run, planned execution, producer slot, outbox).</summary>
    private async Task<long[]> CountsAsync(RunCreationTests.World world)
    {
        var run = await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'");
        var planned = await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where org_id = '{world.Org}'");
        var slots = await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where org_id = '{world.Org}'");
        var outbox = await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org}'");

        return [run, planned, slots, outbox];
    }

    private async Task<long> RunCreateRecordsAsync(RunCreationTests.World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org}' and operation = 'run:create'");

    // ------------------------------------------------------------------ AC3

    [Fact]
    public async Task CreateRun_SameKeySamePayload_ReturnsSameRunId()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);

        var first = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Text("state").ShouldBe("queued");
        var runId = first.Id("run_id");
        var before = await CountsAsync(world);

        var replay = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Text("state").ShouldBe("queued");
        replay.Id("run_id").ShouldBe(runId);
        (await CountsAsync(world)).ShouldBe(before, "a replay writes no run, planned execution, slot or outbox row");
        (await RunCreateRecordsAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// AC3 with the body re-spelled: different whitespace and member order, same canonical form. The request identity is
    /// the RFC 8785 digest of the body, so this is a replay, not a 409. <c>components</c> carries two members so member
    /// order is observable.
    /// </summary>
    [Fact]
    public async Task CreateRun_SameKey_CanonicallyEquivalentBody_Replays()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var first = await RunCreationTests.CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","components":{"a":"1","b":"2"}}""",
            idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await RunCreationTests.CreateRunAsync(
            world,
            $$"""
            {
              "components": { "b": "2", "a": "1" },
              "manifest_id": "{{world.ManifestId}}"
            }
            """,
            idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("run_id").ShouldBe(first.Id("run_id"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC4

    [Fact]
    public async Task CreateRun_SameKeyDifferentPayload_409()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var first = await RunCreationTests.CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","components":{"a":"1"}}""",
            idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var conflict = await RunCreationTests.CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","components":{"a":"2"}}""",
            idempotencyKey: key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'")).ShouldBe(1);
    }

    /// <summary>A failure Result is never stored, so the key is reusable: a first attempt refused with 422 leaves no
    /// record, and the same key with a valid body then creates the run.</summary>
    [Fact]
    public async Task CreateRun_KeyReusable_AfterFailedAttempt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var failed = await RunCreationTests.CreateRunAsync(
            world,
            $$"""{"manifest_id":"{{world.ManifestId}}","release":"v1.0"}""",
            idempotencyKey: key);
        failed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        failed.ErrorCode.ShouldBe("release_unresolvable");
        (await RunCreateRecordsAsync(world)).ShouldBe(0, "a failure Result rolls the begin row back with the transaction");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);

        var retry = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);

        retry.Status.ShouldBe(HttpStatusCode.Created, retry.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ key validation

    [Fact]
    public async Task CreateRun_KeyValidation_MissingAndTooLong_400_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var body = RunCreationTests.RunBody(world.ManifestId);

        var missing = await RunCreationTests.CreateRunAsync(world, body, omitIdempotencyKey: true);
        missing.Status.ShouldBe(HttpStatusCode.BadRequest);

        var tooLong = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: new string('k', 129));
        tooLong.Status.ShouldBe(HttpStatusCode.BadRequest);

        (await RunCreateRecordsAsync(world)).ShouldBe(0);
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);

        var maxLength = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: new string('k', 128));
        maxLength.Status.ShouldBe(HttpStatusCode.Created, maxLength.ErrorMessage);
    }

    // ------------------------------------------------------------------ tenant / project scope

    [Fact]
    public async Task CreateRun_SameKey_IsScopedToOrgAndProject()
    {
        await using var worldA = await RunCreationTests.StartAsync(_fixture, slug: "alpha");
        await using var worldB = await RunCreationTests.StartAsync(_fixture, slug: "beta");
        var key = NewKey();

        var runA = await RunCreationTests.CreateRunAsync(worldA, RunCreationTests.RunBody(worldA.ManifestId), idempotencyKey: key);
        runA.Status.ShouldBe(HttpStatusCode.Created, runA.ErrorMessage);

        // Org B with the same key and its own project/manifest: its own run, never org A's response.
        var runB = await RunCreationTests.CreateRunAsync(worldB, RunCreationTests.RunBody(worldB.ManifestId), idempotencyKey: key);
        runB.Status.ShouldBe(HttpStatusCode.Created, runB.ErrorMessage);
        runB.Id("run_id").ShouldNotBe(runA.Id("run_id"));

        // Same org, a different project, the same key and body: the command's ProjectId is part of the request identity,
        // so this is a different request and never a replay of project A's run. The record is org-scoped (project_id is
        // NULL for a member caller — TenantResolver.ResolveMemberAsync returns ProjectId: null), so the key is already
        // claimed and the mismatch is 409 idempotency_conflict, never project A's run_id.
        var sibling = await CreateProjectAsync(worldA.Client, worldA.User, worldA.Org, "sibling");
        var crossProject = await RunCreationTests.CreateRunAsync(
            worldA,
            RunCreationTests.RunBody(worldA.ManifestId),
            project: sibling,
            idempotencyKey: key);

        crossProject.Status.ShouldBe(HttpStatusCode.Conflict);
        crossProject.ErrorCode.ShouldBe("idempotency_conflict");
        crossProject.Body.TryGetProperty("run_id", out _).ShouldBeFalse();
    }

    // ------------------------------------------------------------------ same-transaction proof

    /// <summary>
    /// The record is written in the run's transaction: a crash after slot registration rolls the begin row back, so the
    /// key is free and the retry creates exactly one run.
    /// </summary>
    [Fact]
    public async Task CreateRun_FailureAfterSlotRegistration_LeavesNoRecord_KeyRetryable()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOutboxWriter>();
            services.AddSingleton<IOutboxWriter, ThrowingOutboxWriter>();
        }));
        using var client = crashing.CreateClient();

        var failed = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: client,
            idempotencyKey: key);

        failed.Status.ShouldBe(HttpStatusCode.InternalServerError);
        (await RunCreateRecordsAsync(world)).ShouldBe(0, "the begin row rolls back with the run");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);

        var retry = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);

        retry.Status.ShouldBe(HttpStatusCode.Created, retry.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ retention + shape

    [Fact]
    public async Task CreateRun_Record_HasOperationProjectHashStatusAndRetention()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        (await _fixture.ScalarAsync<string>(world.Database, $"select operation from public.idempotency_record where key = '{key}'"))
            .ShouldBe("run:create");
        // The record is org-scoped: the ambient tenant scope carries no project for a member caller
        // (TenantResolver.ResolveMemberAsync returns ProjectId: null), so project_id is NULL and the key namespace is the
        // org. The route project still separates requests through the command's ProjectId inside the request hash.
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select org_id from public.idempotency_record where key = '{key}'"))
            .ShouldBe(world.Org);
        (await _fixture.ScalarAsync<bool>(world.Database, $"select project_id is null from public.idempotency_record where key = '{key}'"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<string>(world.Database, $"select request_hash from public.idempotency_record where key = '{key}'"))
            .ShouldStartWith("sha256:");
        (await _fixture.ScalarAsync<int>(world.Database, $"select response_status from public.idempotency_record where key = '{key}'"))
            .ShouldBe(201);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select expires_at >= created_at + interval '24 hours' from public.idempotency_record where key = '{key}'"))
            .ShouldBeTrue();
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task CreateRun_ConcurrentSameKey_OneRunBothAnswers()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);

        var answers = await Task.WhenAll(
            RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key),
            RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Created);
        answers[0].Id("run_id").ShouldBe(answers[1].Id("run_id"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'")).ShouldBe(1);
        (await RunCreateRecordsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ audit

    /// <summary>A replay changed nothing, so it adds no second <c>run.create</c> audit row (the behaviour suppresses it).</summary>
    [Fact]
    public async Task CreateRun_Replay_AddsNoSecondAuditRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);

        (await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key)).Status.ShouldBe(HttpStatusCode.Created);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = 'run.create'"))
            .ShouldBe(1);

        (await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key)).Status.ShouldBe(HttpStatusCode.Created);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = 'run.create'"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ anonymous

    [Fact]
    public async Task CreateRun_Anonymous_401_NoRecord()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var answer = await RunCreationTests.CreateRunAsync(world, "{ not json", _ => { }, idempotencyKey: key);

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await RunCreateRecordsAsync(world)).ShouldBe(0);
    }
}

/// <summary>
/// The request identity of <see cref="CreateRunCommand"/> (05.2.1-T2.1): the RFC 8785 digest of the body, so member
/// order and whitespace are invisible to dedup, with a raw-bytes fallback for a body that cannot be canonicalized.
/// Pure, so it carries no Docker trait and no fixture.
/// </summary>
public sealed class CreateRunCommandBodyHashTests
{
    private static CreateRunCommand Command(string body) =>
        new(Guid.CreateVersion7(), Encoding.UTF8.GetBytes(body), "key");

    [Fact]
    public void BodyHash_IsTheCanonicalDigest_OrderAndWhitespaceInsensitive()
    {
        var compact = Command("""{"manifest_id":"x","components":{"a":"1","b":"2"}}""");
        var respelled = Command("{\n  \"components\": { \"b\": \"2\", \"a\": \"1\" },\n  \"manifest_id\": \"x\"\n}");

        compact.BodyHash.ShouldBe(respelled.BodyHash);
        compact.BodyHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void BodyHash_DifferentBody_Differs()
    {
        Command("""{"manifest_id":"x","components":{"a":"1"}}""").BodyHash
            .ShouldNotBe(Command("""{"manifest_id":"x","components":{"a":"2"}}""").BodyHash);
    }

    [Fact]
    public void BodyHash_UncanonicalizableBody_FallsBackToRawSha256()
    {
        Command("{ not json").BodyHash.ShouldStartWith("raw-sha256:");
    }

    [Fact]
    public void Operation_IsRunCreate()
    {
        IIdempotentCommand command = Command("{}");

        command.Operation.ShouldBe("run:create");
    }
}
