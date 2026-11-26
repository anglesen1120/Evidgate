#pragma warning disable CA1861

using System.Linq;
using System.Net;
using System.Net.Http;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA probes for 05.2.2-T1 (run identity declared by the producer): body-only storage, the
/// <c>ref_name</c>/<c>pr_number</c> bounds, hostile JSON text, the NULL-safe immutability trigger,
/// and the absence of any run mutation route. Each assertion is a consumer-visible outcome, not a re-statement of
/// the implementation.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CreateRunIdentityQaTests : IClassFixture<TenancyHostPool>
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;
    // Reuse one API host per class; each world still owns its tenant data.
    // Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts;

    public CreateRunIdentityQaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    // ------------------------------------------------------------------ declared identity is never verified

    [Fact]
    public async Task Qa_DeclaredBranchRef_EveryMemberIsStoredAndTheRunIsUnverified()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var body = BodyWithRef(world,
            $$"""{"kind":"pull_request","name":"7/merge","pr_number":7,"head_sha":"{{OtherSha}}","trigger_event":"push","is_fork":false}""");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Declared(world.Project), body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "ref_kind")).ShouldBe("pull_request");
        (await ColumnAsync(world, "ref_name")).ShouldBe("7/merge");
        (await ColumnAsync(world, "pr_number")).ShouldBe("7");
        (await ColumnAsync(world, "head_sha")).ShouldBe(OtherSha);
        (await ColumnAsync(world, "base_sha")).ShouldBeNull();
        (await ColumnAsync(world, "trigger_event")).ShouldBe("push");
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
    }

    [Fact]
    public async Task Qa_DeclaredIsForkAlone_LabelsTheRunUnverified()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        // No member is verified, so even a lone is_fork declaration is a stored declared identity.
        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Declared(world.Project), BodyWithRef(world, """{"is_fork":true}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "is_fork")).ShouldBe("true");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
    }

    // ------------------------------------------------------------------ AC4: name bound counts code points

    [Fact]
    public async Task Qa_RefName_255CodePointsWithAstralRunes_IsAccepted_AndCountedAsCodePoints()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        // 255 code points but 510 UTF-16 units: a UTF-16 length check would wrongly reject this.
        var name = string.Concat(Enumerable.Repeat("\U0001F600", 255));
        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, $$"""{"kind":"branch","name":"{{name}}"}"""));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<int>(
            world.Database, $"select char_length(ref_name) from public.run where org_id = '{world.Org}'")).ShouldBe(255);
    }

    [Fact]
    public async Task Qa_RefName_256CodePointsWithAstralRunes_IsRequestInvalid_AndWritesNothing()
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture, hosts: _hosts);

        var name = string.Concat(Enumerable.Repeat("\U0001F600", 256));
        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, $$"""{"kind":"branch","name":"{{name}}"}"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ AC4: pr_number bounds and JSON types

    [Fact]
    public async Task Qa_PrNumber_Int32MaxIsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(
            world, BodyWithRef(world, """{"kind":"pull_request","pr_number":2147483647}"""));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await ColumnAsync(world, "pr_number")).ShouldBe("2147483647");
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-1")]
    [InlineData("5.0")]
    [InlineData("\"5\"")]
    [InlineData("1e400")]
    public async Task Qa_PrNumber_OutOfRangeOrWrongJsonType_IsRequestInvalid_NotA500(string prNumber)
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(
            world, BodyWithRef(world, $$"""{"kind":"pull_request","pr_number":{{prNumber}}}"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ AC4: hostile JSON text inside ref

    [Theory]
    [InlineData("""{"kind":"branch","name":"a\u0000b"}""")]
    [InlineData("""{"kind":"branch","name":"\ud800"}""")]
    [InlineData("""{"kind":"branch","name":"main","trigger_event":"push\u0000"}""")]
    public async Task Qa_NulOrLoneSurrogate_InsideRef_IsRequestInvalid_AndWritesNothing(string refJson)
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(world, BodyWithRef(world, refJson));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ AC5: the trigger is NULL-safe

    [Fact]
    public async Task Qa_OwnerUpdate_SettingIdentityOnARunWithoutIdentity_FailsWithP0001()
    {
        // A NULL -> value change on a row without identity: a naive `old <> new` is NULL here and would let it pass.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        foreach (var assignment in new[] { "ref_name = 'x'", "pr_number = 5", "is_fork = true", "unverified_ref = true" })
        {
            await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await owner.CaptureErrorAsync($"update public.run set {assignment} where id = '{runId}'");
            error.SqlState.ShouldBe("P0001", assignment);
        }

        (await ColumnAsync(world, "ref_name")).ShouldBeNull();
        (await ColumnAsync(world, "pr_number")).ShouldBeNull();
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("false");
    }

    [Fact]
    public async Task Qa_OwnerUpdate_NullToNullOnAnIdentityColumn_Succeeds()
    {
        // The other half of NULL safety: `is distinct from` must not treat NULL = NULL as a change.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync($"update public.run set ref_name = ref_name, head_sha = head_sha where id = '{runId}'"))
            .ShouldBe(1);
        await owner.CommitAsync();
    }

    // ------------------------------------------------------------------ AC5: no mutation route

    [Theory]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Qa_RunRoute_HasNoMutationVerb_AndTheIdentityIsUnchanged(string verb)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, Declared(world.Project), BodyWithRef(world, $$"""{"kind":"branch","name":"main","head_sha":"{{Sha}}","trigger_event":"push"}"""));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var runId = await _fixture.ScalarAsync<Guid>(
            world.Database, $"select id from public.run where org_id = '{world.Org}'");

        var answer = await world.Client.SendAsync(new HttpMethod(verb), $"/v1/runs/{runId:D}", world.Admin);

        new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed }.ShouldContain(answer.Status, verb);
        (await ColumnAsync(world, "ref_name")).ShouldBe("main");
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
    }

    // ------------------------------------------------------------------ helpers (the same shape as CreateRunIdentityTests)

    private static string BodyWithRef(RunCreationTests.World world, string refJson) =>
        $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{{{refJson}}}}""";

    /// <summary>An ingest-token principal: its credential id is the stable token id, never a ref claim.</summary>
    private static RequestPrincipal Declared(Guid project) =>
        RequestPrincipal.Service(project, ["run:create"], credentialId: Guid.CreateVersion7().ToString("D"));

    private async Task<string?> ColumnAsync(RunCreationTests.World world, string column) =>
        await _fixture.ScalarAsync<string>(
            world.Database, $"select {column}::text from public.run where org_id = '{world.Org}'");
}
