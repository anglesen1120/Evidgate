using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CreateRunIdentityTests
{
    // ------------------------------------------------------------------ AC4: shape validation

    [Fact]
    public async Task CreateRun_InvalidRef_422RequestInvalidAndNoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        (string Ref, string Path)[] cases =
        [
            ("\"branch\"", "/ref"),
            ("3", "/ref"),
            ("null", "/ref"),
            ("""{"kind":"commit"}""", "/ref/kind"),
            ("""{"kind":3}""", "/ref/kind"),
            ("""{"trigger_event":"workflow_dispatch"}""", "/ref/trigger_event"),
            ("""{"name":""}""", "/ref/name"),
            ($$$"""{"name":"{{{new string('x', 256)}}}"}""", "/ref/name"),
            ($$$"""{"head_sha":"{{{new string('a', 41)}}}"}""", "/ref/head_sha"),
            ($$$"""{"base_sha":"{{{new string('A', 40)}}}"}""", "/ref/base_sha"),
            ("""{"head_sha":42}""", "/ref/head_sha"),
            ("""{"pr_number":0}""", "/ref/pr_number"),
            ("""{"pr_number":"12"}""", "/ref/pr_number"),
            ("""{"is_fork":"yes"}""", "/ref/is_fork"),
            ("""{"head_commit":"abc"}""", "/ref/head_commit"),
            // The only rule that runs on the effective values: a body-only pull_request without a number.
            ("""{"kind":"pull_request","name":"12/merge"}""", "/ref/pr_number"),
        ];

        foreach (var (refJson, path) in cases)
        {
            var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, refJson));

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refJson);
            answer.ErrorCode.ShouldBe("request_invalid", refJson);
            answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .Select(detail => detail.GetProperty("path").GetString())
                .ShouldContain(path, refJson);
        }

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    public async Task CreateRun_RefNameWithAControlCharacter_422RequestInvalidAndNoRows(string name)
    {
        // A git ref name cannot carry a C0/C1 control or DEL, so the body's ref_name is refused invalid_value like any
        // other bad name, before any write (SEC-F3). (NUL and a lone surrogate are refused even earlier, by the
        // unstorable-text pass; see CreateRunIdentityQaTests.)
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, JsonSerializer.Serialize(new { name })));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("path").GetString())
            .ShouldContain("/ref/name");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    [Fact]
    public async Task CreateRun_ValidBodyRef_IsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var body = BodyWithRef(
            world,
            $$$"""{"kind":"branch","name":"{{{new string('x', 255)}}}","pr_number":12,"head_sha":"{{{Sha}}}","base_sha":"{{{OtherSha}}}","trigger_event":"manual","is_fork":true}""");

        var answer = await RunCreationTests.CreateRunAsync(world, body);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();
    }

    // ------------------------------------------------------------------ AC9: idempotency identity

    [Fact]
    public async Task CreateRun_Replay_SameBodyFromRenewedToken_ReplaysTheStoredRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = DeclaredBranchBody(world);
        var tokenId = Guid.CreateVersion7().ToString("D");

        var first = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, RequestPrincipal.Service(world.Project, ["run:create"], credentialId: tokenId), body, key);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);

        // The request identity is the body digest alone, so a retry under any credential of the project replays.
        var replay = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, RequestPrincipal.Service(world.Project, ["run:create"], credentialId: Guid.CreateVersion7().ToString("D")), body, key);

        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value.RunId.ShouldBe(first.Value.RunId);
        replay.Value.UnverifiedRef.ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.idempotency_record where operation = 'run:create' and org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateRun_SameKeyDifferentBodyRef_409()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var first = await RunCreationTests.CreateRunAsync(
            world,
            BodyWithRef(world, """{"kind":"branch","name":"main"}"""),
            idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        // The RFC 8785 body digest covers the ref block, so a different identity under the same key conflicts.
        var conflict = await RunCreationTests.CreateRunAsync(
            world,
            BodyWithRef(world, """{"kind":"branch","name":"release"}"""),
            idempotencyKey: key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateRun_ReplayedBodyRef_ReturnsTheSameUnverifiedLabel()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = BodyWithRef(world, """{"kind":"branch","name":"main","trigger_event":"push"}""");

        var first = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();

        var replay = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("run_id").ShouldBe(first.Id("run_id"));
        replay.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue("the stored response is replayed verbatim");
    }
}
