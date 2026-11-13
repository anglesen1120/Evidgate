// 04.1.3-T1 (AC1, AC3) on the paths the criteria name: `POST /v1/projects/{p}/manifests` and
// `POST /v1/projects/{p}/runs`, plus the body reader on its own. Each cap is the configured one, so a test boots a
// second host that overrides only that value — the world itself is arranged under the defaults, so nothing the
// arrange step uploads is refused by the cap under test — and every refusal is asserted to write nothing.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The manifest caps: a body counted as it is read (so a missing or short <c>Content-Length</c> cannot raise it, and
/// no more than the cap plus one byte is ever pulled off the stream), and the declared executions/variants counted
/// after the schema parse but before anything is expanded or written.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed partial class IngestCapsTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture = fixture;
    // Reuse one API host per class; each world still owns its tenant data.
    // Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts = hosts;

    // ------------------------------------------------------------------ body reader, on its own

    /// <summary>A declared length is never trusted: a body that lies short is still counted, and refused at the cap.</summary>
    [Fact]
    public async Task ReadBodyAsync_DeclaredLengthShorterThanTheBody_StillCountsAndRefuses()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 400)));
        context.Request.ContentLength = 10;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 100,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        bytes.IsEmpty.ShouldBeTrue("a refused body is never handed on");
        tooLarge.ShouldNotBeNull();
        tooLarge.Code.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = tooLarge.Details.ShouldHaveSingleItem();
        detail.Path.ShouldBe("manifest_bytes");
        detail.Limit.ShouldBe(100);
        detail.Actual.ShouldNotBeNull();
        detail.Actual.Value.ShouldBeGreaterThan(100, "the count is measured, not taken from the header");
    }

    /// <summary>A declared length over the cap refuses before a byte of the stream is read, and reports that declaration.</summary>
    [Fact]
    public async Task ReadBodyAsync_DeclaredLengthOverTheCap_RefusesWithoutReading()
    {
        var source = new CountingStream(new byte[1024 * 1024]);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = 1024 * 1024;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 1024,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        bytes.IsEmpty.ShouldBeTrue();
        tooLarge.ShouldNotBeNull();
        tooLarge.Details.ShouldHaveSingleItem().Actual.ShouldBe(1024 * 1024, "the declaration is the value the server has");
        source.BytesRead.ShouldBe(0, "the declaration alone refuses it");
    }

    /// <summary>The host's own body-size limit is cleared so the route's counter answers, and a read-only one is left alone.</summary>
    [Fact]
    public void AllowCounterToOwnTheRefusal_ClearsTheHostLimit()
    {
        var feature = new BodySizeFeature(maxRequestBodySize: 30_000_000);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        OrchestratorEndpoints.AllowCounterToOwnTheRefusal(context);

        feature.MaxRequestBodySize.ShouldBeNull("the counter owns the refusal, at the configured cap and with its detail");

        var readOnly = new BodySizeFeature(maxRequestBodySize: 30_000_000, readOnly: true);
        var settled = new DefaultHttpContext();
        settled.Features.Set<IHttpMaxRequestBodySizeFeature>(readOnly);

        OrchestratorEndpoints.AllowCounterToOwnTheRefusal(settled);

        readOnly.MaxRequestBodySize.ShouldBe(30_000_000, "a feature the host already fixed is not rewritten");
    }

    /// <summary>
    /// A chunked body (no <c>Content-Length</c>) is counted as it arrives and refused at the cap, and the reader stops
    /// there: the stream is never pulled past the one byte that proves the overrun.
    /// </summary>
    [Fact]
    public async Task ReadBodyAsync_ChunkedBodyOverTheCap_StopsOneBytePastTheCap()
    {
        var source = new CountingStream(new byte[8 * 1024 * 1024]);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = null;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 1000,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        bytes.IsEmpty.ShouldBeTrue();
        tooLarge.ShouldNotBeNull();
        tooLarge.Details.ShouldHaveSingleItem().Limit.ShouldBe(1000);
        source.BytesRead.ShouldBe(1001, "bounded memory: the stream is never drained, and never read past the cap plus one");
    }

    /// <summary>A body exactly at the cap is accepted whole; one byte more is refused.</summary>
    [Fact]
    public async Task ReadBodyAsync_AtTheCap_IsAccepted()
    {
        var body = new byte[1000];
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 1000,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        tooLarge.ShouldBeNull();
        bytes.Length.ShouldBe(1000);
    }

    // ------------------------------------------------------------------ the manifest body cap on the routes

    /// <summary>
    /// AC1: a manifest body exactly at the configured cap is accepted on the upload route — the boundary is inclusive
    /// — whichever of the two run routes the same reader serves.
    /// </summary>
    [Fact]
    public async Task UploadManifest_BodyAtTheCap_IsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var manifest = ManifestBody(world);
        var cap = Encoding.UTF8.GetByteCount(manifest);

        await using var capped = CappedHost(world, ("MaxManifestBytes", cap));

        var answer = await UploadManifestAsync(capped.Client, world, manifest);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
    }

    /// <summary>
    /// A manifest body one byte over the cap is 413 <c>payload_too_large</c> with
    /// <c>details[] {path: manifest_bytes, reason, limit, actual}</c> and no manifest row written. The body declares
    /// its length, so the detail carries that declaration — the length the server refused without reading.
    /// </summary>
    [Fact]
    public async Task UploadManifest_BodyOverTheCap_413WithCapDetail_NoManifestCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        // A plan of its own, so an accepted upload would add a manifest row the assertion below can see.
        var manifest = ManifestBody(world, variants: 1, shards: 1);
        var declared = Encoding.UTF8.GetByteCount(manifest);
        var cap = declared - 1;

        await using var capped = CappedHost(world, ("MaxManifestBytes", cap));

        var answer = await UploadManifestAsync(capped.Client, world, manifest);

        answer.Status.ShouldBe((HttpStatusCode)413, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_bytes");
        detail.GetProperty("reason").GetString().ShouldBe(ApiErrorCode.PayloadTooLarge);
        detail.GetProperty("limit").GetInt64().ShouldBe(cap);
        detail.GetProperty("actual").GetInt64().ShouldBe(declared, "the declared length is the value the server refused");
        (await ManifestCountAsync(world)).ShouldBe(1, "only the manifest the world arranged exists");
    }

    /// <summary>
    /// A manifest body with no <c>Content-Length</c> is counted as it arrives: 413 with the same cap detail, whose
    /// <c>actual</c> is the measured count past the limit, and no manifest row written.
    /// </summary>
    [Fact]
    public async Task UploadManifest_ChunkedBodyOverTheCap_413WithMeasuredDetail_NoManifestCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var manifest = ManifestBody(world, variants: 1, shards: 1);
        var cap = Encoding.UTF8.GetByteCount(manifest) / 2;

        await using var capped = CappedHost(world, ("MaxManifestBytes", cap));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{world.Project}/manifests");
        world.Admin(request.Headers);
        request.Headers.Add("Idempotency-Key", RegistryApi.NewKey());
        request.Content = new ChunkedJson(manifest);

        using var response = await capped.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var answer = new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);

        answer.Status.ShouldBe((HttpStatusCode)413, text);
        answer.ErrorCode.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_bytes");
        detail.GetProperty("limit").GetInt64().ShouldBe(cap);
        detail.GetProperty("actual").GetInt64().ShouldBeGreaterThan(cap, "the count is measured, not taken from a header");
        (await ManifestCountAsync(world)).ShouldBe(1, "only the manifest the world arranged exists");
    }

    /// <summary>
    /// The run-create route has its own small transport cap: a chunked body over it is 413 with
    /// <c>path: run_body_bytes</c> and no run, planned execution, producer slot or outbox row.
    /// </summary>
    [Fact]
    public async Task CreateRun_ChunkedBodyOverTheCap_413WithCapDetail_NoRowCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var body = RunCreationTests.RunBody(world.ManifestId) + new string(' ', OrchestratorEndpoints.MaxRunBytes);
        Encoding.UTF8.GetByteCount(body).ShouldBeGreaterThan(OrchestratorEndpoints.MaxRunBytes, "the body must exceed the transport cap");

        using var content = new ChunkedJson(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{world.Project}/runs");
        world.Admin(request.Headers);
        request.Headers.Add("Idempotency-Key", RegistryApi.NewKey());
        request.Content = content;

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var answer = new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);

        answer.Status.ShouldBe((HttpStatusCode)413, text);
        answer.ErrorCode.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("run_body_bytes");
        detail.GetProperty("limit").GetInt64().ShouldBe(OrchestratorEndpoints.MaxRunBytes);
        detail.GetProperty("actual").GetInt64().ShouldBeGreaterThan(OrchestratorEndpoints.MaxRunBytes);

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ executions and variants

    /// <summary>AC1: a manifest whose declared executions equal the cap is accepted and planned.</summary>
    [Fact]
    public async Task CreateRun_ExecutionsAtTheCap_IsAccepted()
    {
        // Two cases × two variants × one environment is four planned executions.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await using var capped = CappedHost(world, ("MaxManifestExecutions", 4));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
    }

    /// <summary>
    /// AC1: one execution over the cap is 422 <c>cap_exceeded</c> with <c>{path: manifest_executions, limit, actual}</c>,
    /// refused before the planned-execution list is expanded, and no row exists afterwards.
    /// </summary>
    [Fact]
    public async Task CreateRun_OneExecutionOverTheCap_422CapExceeded_NoRowCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await using var capped = CappedHost(world, ("MaxManifestExecutions", 3));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.CapExceeded);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_executions");
        detail.GetProperty("reason").GetString().ShouldBe(ApiErrorCode.CapExceeded);
        detail.GetProperty("limit").GetInt64().ShouldBe(3);
        detail.GetProperty("actual").GetInt64().ShouldBe(4);

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    /// <summary>
    /// AC1: the variant axis is capped on its own — a manifest under the executions cap whose case declares more
    /// variants than allowed is 422 <c>cap_exceeded</c> naming <c>manifest_variants</c>.
    /// </summary>
    [Fact]
    public async Task CreateRun_VariantsOverTheCap_422CapExceeded_NoRowCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await using var capped = CappedHost(world, ("MaxManifestVariants", 1));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.CapExceeded);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_variants");
        detail.GetProperty("limit").GetInt64().ShouldBe(1);
        detail.GetProperty("actual").GetInt64().ShouldBe(2);

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    /// <summary>The variant axis at its cap is accepted: two variants under a cap of two create the run.</summary>
    [Fact]
    public async Task CreateRun_VariantsAtTheCap_IsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await using var capped = CappedHost(world, ("MaxManifestVariants", 2));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where org_id = '{world.Org}'"))
            .ShouldBe(4, "two cases × two variants × one environment");
    }

    /// <summary>
    /// Each refusal is counted exactly once under the cap that refused it, on the counter's closed label vocabulary, so
    /// an operator can tell which cap is firing without reading a body.
    /// </summary>
    [Fact]
    public async Task CapRefusals_CountEachCapExactlyOnce()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var manifest = ManifestBody(world);
        await using var capped = CappedHost(
            world,
            ("MaxManifestBytes", Encoding.UTF8.GetByteCount(manifest) - 1),
            ("MaxManifestVariants", 1));

        using var listener = IngestTelemetryCapture.Caps(out ConcurrentBag<string> caps);

        var upload = await UploadManifestAsync(capped.Client, world, manifest);
        upload.Status.ShouldBe((HttpStatusCode)413, upload.ErrorMessage);

        var run = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);
        run.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, run.ErrorMessage);

        caps.Count(cap => cap == "manifest_bytes").ShouldBe(1);
        caps.Count(cap => cap == "manifest_variants").ShouldBe(1);
    }

    /// <summary>
    /// A plan past the retired schema bound is a legal manifest: the schema admits every count the configured
    /// execution cap can be raised to, so the runtime cap — not a schema bound — refuses a plan over its value.
    /// </summary>
    [Fact]
    public async Task UploadManifest_TenThousandAndOneExecutions_IsAccepted()
    {
        // 73 cases × 137 variants × one environment is 10 001 planned executions, each covered once.
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 73, hosts: _hosts);

        var manifest = RunCreationTests.Manifest(world.SuiteRevision, world.Cases, variants: 137, shards: 1);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();
        manifest["planned_executions"]!.AsArray().Count.ShouldBe(10_001);

        var answer = await UploadManifestAsync(world.Client, world, manifest.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<int>(world.Database, $"select planned_count from public.manifest where id = '{answer.Id("manifest_id")}'"))
            .ShouldBe(10_001);
    }
}
