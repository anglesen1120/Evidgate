using System.Net;
using System.Text;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class CancelRunEdgeCaseTests(PostgresTenancyFixture fixture)
{
    [Theory]
    [InlineData("r", 1000)]
    [InlineData("ệ", 1000)]
    [InlineData("🙂", 501)]
    [InlineData("🙂", 1000)]
    public async Task CancelRun_ReasonWithinCharacterLimit_AcceptedAndPreserved(string character, int count)
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var reason = string.Concat(Enumerable.Repeat(character, count));
        var body = $$"""{"reason":"{{reason}}"}""";
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(4096);

        var answer = await CancelAsync(world, runId, body);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await RequestCountAsync(world, runId)).ShouldBe(1);
        (await fixture.ScalarAsync<string>(
            world.Database,
            $"select reason from public.run_cancel_request where org_id = '{world.Org}' and run_id = '{runId}'"))
            .ShouldBe(reason);
    }

    [Theory]
    [InlineData("""{"reason":""}""")]
    [InlineData("""{"reason":"\t\r\n"}""")]
    [InlineData("""{"reason":"\u00a0"}""")]
    [InlineData("""{"reason":"\u0000"}""")]
    [InlineData("""{"reason":"\ud800"}""")]
    [InlineData("""{"reason":42}""")]
    [InlineData("""{"reason":true}""")]
    [InlineData("""{"reason":[]}""")]
    [InlineData("""{"reason":{}}""")]
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("{} trailing")]
    public async Task CancelRun_InvalidReasonOrJson_422WithoutSideEffects(string body)
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var before = await SnapshotAsync(world, runId);

        var answer = await CancelAsync(world, runId, body);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await SnapshotAsync(world, runId)).ShouldBe(before);
        var control = await CancelAsync(world, runId, """{"reason":"valid reason"}""");
        control.Status.ShouldBe(HttpStatusCode.Accepted, control.ErrorMessage);
    }

    [Theory]
    [InlineData(4096, HttpStatusCode.Accepted)]
    [InlineData(4097, HttpStatusCode.RequestEntityTooLarge)]
    public async Task CancelRun_JsonAtTransportBoundary_EnforcesExactByteCap(int bytes, HttpStatusCode expected)
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var body = "{}" + new string(' ', bytes - 2);
        Encoding.UTF8.GetByteCount(body).ShouldBe(bytes);
        var before = await SnapshotAsync(world, runId);

        var answer = await CancelAsync(world, runId, body);

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        if (expected == HttpStatusCode.Accepted)
        {
            (await RequestCountAsync(world, runId)).ShouldBe(1);
            (await StateAsync(world, runId)).ShouldBe("canceled");
        }
        else
        {
            answer.ErrorCode.ShouldBe("payload_too_large");
            (await SnapshotAsync(world, runId)).ShouldBe(before);
            (await CancelAsync(world, runId, "{}")).Status.ShouldBe(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task CancelRun_SameKeyAndBody_ReplaysWithoutSideEffects()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var key = NewKey();
        const string body = """{"reason":"stop"}""";
        var first = await CancelAsync(world, runId, body, key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, runId);

        var replay = await CancelAsync(world, runId, body, key);

        replay.Status.ShouldBe(first.Status, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await SnapshotAsync(world, runId)).ShouldBe(before);
        (await RequestCountAsync(world, runId)).ShouldBe(1);
    }

    [Fact]
    public async Task CancelRun_SameKeyAndEquivalentJson_ReplaysCanonicalRequest()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var key = NewKey();
        var first = await CancelAsync(world, runId, """{"reason":"stop"}""", key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, runId);

        var replay = await CancelAsync(world, runId, """{ "reason" : "stop" }""", key);

        replay.Status.ShouldBe(HttpStatusCode.Accepted, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await SnapshotAsync(world, runId)).ShouldBe(before);
    }

    [Fact]
    public async Task CancelRun_SameKeyAndDifferentReason_409WithoutChangingRequest()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var key = NewKey();
        var first = await CancelAsync(world, runId, """{"reason":"first"}""", key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, runId);

        var conflict = await CancelAsync(world, runId, """{"reason":"second"}""", key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await SnapshotAsync(world, runId)).ShouldBe(before);
    }

    [Fact]
    public async Task CancelRun_SameKeyAndDifferentRun_409LeavesSecondRunQueued()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var firstRun = await CreateRunAsync(world);
        var secondRun = await CreateRunAsync(world);
        var key = NewKey();
        var first = await CancelAsync(world, firstRun, "{}", key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, secondRun);

        var conflict = await CancelAsync(world, secondRun, "{}", key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await SnapshotAsync(world, secondRun)).ShouldBe(before);
        (await StateAsync(world, secondRun)).ShouldBe("queued");
        (await RequestCountAsync(world, secondRun)).ShouldBe(0);
        (await CancelAsync(world, secondRun, "{}")).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task CancelRun_RolelessMember_403BeforeAnyStateChange()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CreateRunAsync(world);
        var member = Guid.CreateVersion7();
        var email = $"cancel-{member:N}@example.test";
        await fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{member}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
              values (gen_random_uuid(), '{world.Org}', '{member}', '{email}', 'active')
            """);
        var before = await SnapshotAsync(world, runId);
        var caller = TenancyApiFactory.Resolved(user: member, orgHeader: world.Org);

        var denied = await world.Client.SendRawAsync(
            HttpMethod.Post,
            $"/v1/runs/{runId:D}/cancel",
            headers =>
            {
                caller(headers);
                headers.TryAddWithoutValidation("Idempotency-Key", NewKey());
            },
            "{}");

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        (await SnapshotAsync(world, runId)).ShouldBe(before);
        (await CancelAsync(world, runId, "{}")).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    private static Task<ApiAnswer> CancelAsync(
        RunCreationTests.World world,
        Guid runId,
        string body,
        string? key = null) =>
        world.Client.SendRawAsync(
            HttpMethod.Post,
            $"/v1/runs/{runId:D}/cancel",
            headers =>
            {
                world.Admin(headers);
                headers.TryAddWithoutValidation("Idempotency-Key", key ?? NewKey());
            },
            body);

    private static async Task<Guid> CreateRunAsync(RunCreationTests.World world)
    {
        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        return answer.Id("run_id");
    }

    private Task<string> StateAsync(RunCreationTests.World world, Guid runId) =>
        fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId}'");

    private Task<long> RequestCountAsync(RunCreationTests.World world, Guid runId) =>
        fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_cancel_request where org_id = '{world.Org}' and run_id = '{runId}'");

    private Task<string> SnapshotAsync(RunCreationTests.World world, Guid runId) =>
        fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select jsonb_build_object(
              'state', r.state,
              'version', r.version,
              'requests', (select count(*) from public.run_cancel_request c where c.org_id = r.org_id and c.run_id = r.id),
              'outbox', (select count(*) from public.outbox o where o.aggregate_id = r.id),
              'audit', (select count(*) from public.audit_log a where a.org_id = r.org_id and a.object_type = 'run' and a.object_id = '{runId}'))::text
              from public.run r where r.org_id = '{world.Org}' and r.id = '{runId}'
            """);
}
