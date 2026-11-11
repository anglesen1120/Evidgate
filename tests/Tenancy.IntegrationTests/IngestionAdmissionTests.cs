using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestionAdmissionTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private const int WindowSeconds = 60;
    private const int TransportCap = 1_048_576;

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.77")]
    [InlineData("X-Project-Id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("X-Token-Jti", "forged-token-id")]
    [InlineData("X-Test-Service-Scope", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")]
    [InlineData("Authorization", "Bearer forged-not-a-credential")]
    public async Task Ingest_SpoofedPartitionHeader_DoesNotRestoreBudget(string header, string value)
    {
        var clock = Clock();
        await using var world = await StartAsync(clock);
        var body = Batch(world, Guid.CreateVersion7(), sequence: 1);
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body)).Status.ShouldBe(HttpStatusCode.OK);

        var refused = await IngestQaRequests.PostAsync(
            world.Client, world.RunId, IngestQaRequests.JsonBody(body),
            mutate: request => request.Headers.TryAddWithoutValidation(header, value));
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("rate_limited");

        clock.Advance(TimeSpan.FromSeconds(WindowSeconds));
        var recovered = await IngestQaRequests.PostAsync(
            world.Client, world.RunId, IngestQaRequests.JsonBody(body),
            mutate: request => request.Headers.TryAddWithoutValidation(header, value));
        recovered.Status.ShouldBe(HttpStatusCode.OK, recovered.ErrorMessage);
    }

    [Fact]
    public async Task Ingest_NewTokenInSameProject_SharesProjectBudget()
    {
        var clock = Clock();

        // One permit for the token and one for the whole project: the second token has its own budget free
        // and is refused by the project's alone, which is what this case is about.
        var limiter = Limiter(clock, permitLimit: 1, projectPermitLimit: 1);
        await using var world = await StartAsync(clock, limiter: limiter, slots: 2);
        var body = Batch(world, Guid.CreateVersion7(), sequence: 1);
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body)).Status.ShouldBe(HttpStatusCode.OK);
        await using var otherHost = hosts.WithPrincipal(
            world.Database, world.Org, world.Project,
            IngestTestWorld.Writer(IngestTestWorld.OtherJti, world.Project),
            services: services => services.AddSingleton(limiter));
        using var otherClient = otherHost.CreateClient();
        var otherBody = IngestQaRequests.Batch(IngestQaRequests.Event(
            Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 1, producerId: "shard-2"));

        var refused = await IngestQaRequests.PostEventsAsync(otherClient, world.RunId, otherBody);
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("rate_limited");

        clock.Advance(TimeSpan.FromSeconds(WindowSeconds));
        (await IngestQaRequests.PostEventsAsync(otherClient, world.RunId, otherBody)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ingest_SameTokenInDifferentProjects_DoesNotShareBudget()
    {
        var clock = Clock();
        var limiter = Limiter(clock, permitLimit: 1);
        await using var first = await StartAsync(clock, limiter: limiter);
        await using var second = await StartAsync(clock, limiter: limiter);
        first.Project.ShouldNotBe(second.Project);
        var firstBody = Batch(first, Guid.CreateVersion7(), sequence: 1);
        var secondBody = Batch(second, Guid.CreateVersion7(), sequence: 1);
        (await IngestQaRequests.PostEventsAsync(first.Client, first.RunId, firstBody)).Status.ShouldBe(HttpStatusCode.OK);
        (await IngestQaRequests.PostEventsAsync(first.Client, first.RunId, firstBody)).Status.ShouldBe(HttpStatusCode.TooManyRequests);

        (await IngestQaRequests.PostEventsAsync(second.Client, second.RunId, secondBody)).Status.ShouldBe(HttpStatusCode.OK);
        (await IngestQaRequests.PostEventsAsync(second.Client, second.RunId, secondBody)).Status.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Ingest_ProjectATokenOnProjectBRun_RefusesWithoutSpendingProjectBBudget()
    {
        var clock = Clock();
        var limiter = Limiter(clock, permitLimit: 1);
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);
        await using var world = await StartAsync(clock, limiter: limiter);
        using var setup = world.Api.CreateClient();
        var projectB = await RegistryApi.CreateProjectAsync(setup, world.User, world.Org, Unique.Slug("other"));
        var planB = await world.SeedRunInProjectAsync(projectB);
        await IngestTestWorld.SeedSlotsAsync(fixture, world.Database, world.Org, planB.RunId, 1, boundJti: null);
        var eventId = Guid.CreateVersion7();
        var body = IngestQaRequests.Batch(IngestQaRequests.Event(eventId, planB.Executions[0].ExecutionKey, sequence: 1));

        var refused = await IngestQaRequests.PostEventsAsync(world.Client, planB.RunId, body);
        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("project_scope_mismatch");
        refused.Headers.Contains("Retry-After").ShouldBeFalse();
        (await world.CountAsync("event", $"event_id = '{eventId:D}'")).ShouldBe(0);
        (await world.CountAsync("event_batch", $"run_id = '{planB.RunId:D}'")).ShouldBe(0);

        await using var hostB = hosts.WithPrincipal(
            world.Database, world.Org, projectB,
            IngestTestWorld.Writer(projectScope: projectB), services: services => services.AddSingleton(limiter));
        using var clientB = hostB.CreateClient();
        (await IngestQaRequests.PostEventsAsync(clientB, planB.RunId, body)).Status.ShouldBe(HttpStatusCode.OK);
        var chargedA = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, Batch(world, Guid.CreateVersion7(), 1));
        chargedA.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        chargedA.ErrorCode.ShouldBe("rate_limited");

        // AC7: the pipeline's 403 is counted once, under the vocabulary label and not its wire code.
        reasons.Count(reason => string.Equals(reason, "forbidden", StringComparison.Ordinal)).ShouldBe(1);
        reasons.Count(reason => string.Equals(reason, "project_scope_mismatch", StringComparison.Ordinal)).ShouldBe(0);
    }

    [Fact]
    public async Task Ingest_RateRefusal_WritesNoRowsForUniqueEventOrRequest()
    {
        var clock = Clock();
        await using var world = await StartAsync(clock);
        var acceptedId = Guid.CreateVersion7();
        var acceptedRequest = Guid.CreateVersion7().ToString("D");
        (await IngestQaRequests.PostEventsAsync(
            world.Client, world.RunId, Batch(world, acceptedId, 1), acceptedRequest)).Status.ShouldBe(HttpStatusCode.OK);
        await AssertRowsAsync(world, acceptedId, acceptedRequest, expected: 1);
        var refusedId = Guid.CreateVersion7();
        var refusedRequest = Guid.CreateVersion7().ToString("D");
        var body = Batch(world, refusedId, 2);

        var refused = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body, refusedRequest);
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        await AssertRowsAsync(world, refusedId, refusedRequest, expected: 0);
        await AssertRowsAsync(world, acceptedId, acceptedRequest, expected: 1);

        clock.Advance(TimeSpan.FromSeconds(WindowSeconds));
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body, refusedRequest)).Status.ShouldBe(HttpStatusCode.OK);
        await AssertRowsAsync(world, refusedId, refusedRequest, expected: 1);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 60)]
    [InlineData(59_000, 1)]
    [InlineData(59_999, 1)]
    public async Task Ingest_RetryAfter_RoundsUpRemainingWindowAndDisappearsOnSuccess(int elapsedMs, int expectedSeconds)
    {
        var clock = Clock();
        await using var world = await StartAsync(clock);
        var body = Batch(world, Guid.CreateVersion7(), sequence: 1);
        var accepted = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        accepted.Headers.Contains("Retry-After").ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));

        var refused = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        var raw = refused.Headers.GetValues("Retry-After").ShouldHaveSingleItem();
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds).ShouldBeTrue();
        seconds.ShouldBeGreaterThanOrEqualTo(1);
        seconds.ShouldBe(expectedSeconds);

        clock.Advance(TimeSpan.FromMilliseconds((WindowSeconds * 1000) - elapsedMs));
        var boundary = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        boundary.Status.ShouldBe(HttpStatusCode.OK, boundary.ErrorMessage);
        boundary.Headers.Contains("Retry-After").ShouldBeFalse();
    }

    [Theory]
    [InlineData("declared", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("chunked", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("gzip-ratio", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("gzip-invalid", HttpStatusCode.UnprocessableEntity)]
    public async Task Ingest_TransportRefusal_ConsumesBudgetBeforeValidation(string kind, HttpStatusCode expected)
    {
        var clock = Clock();
        await using var world = await StartAsync(clock);
        var body = Batch(world, Guid.CreateVersion7(), sequence: 1);
        var refused = await IngestQaRequests.PostAsync(world.Client, world.RunId, RefusedContent(kind, body));
        refused.Status.ShouldBe(expected, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe(expected == HttpStatusCode.RequestEntityTooLarge ? "batch_too_large" : "request_invalid");
        refused.Headers.Contains("Retry-After").ShouldBeFalse();
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(0);

        var spent = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        spent.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        spent.ErrorCode.ShouldBe("rate_limited");
        clock.Advance(TimeSpan.FromSeconds(WindowSeconds));
        var accepted = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Headers.Contains("Retry-After").ShouldBeFalse();
    }

    [Fact]
    public async Task Ingest_ValidGzip_ConsumesOneRequestNotCompressedBytes()
    {
        var clock = Clock();
        await using var world = await StartAsync(clock, permitLimit: 2);
        var body = Batch(world, Guid.CreateVersion7(), sequence: 1);
        (await IngestQaRequests.PostAsync(world.Client, world.RunId, Gzip(IngestQaRequests.Json(body))))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body)).Status.ShouldBe(HttpStatusCode.OK);
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body)).Status.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Ingest_ExhaustedBudget_DoesNotReadRequestBody()
    {
        var clock = Clock();
        await using var world = await StartAsync(clock);
        var bytes = Encoding.UTF8.GetBytes(IngestQaRequests.Json(Batch(world, Guid.CreateVersion7(), 1)));
        using var acceptedBody = new ReadProbeStream(bytes);
        var accepted = await world.Host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = $"/v1/runs/{world.RunId:D}/events";
            context.Request.ContentType = "application/json";
            context.Request.Body = acceptedBody;
        }, TestContext.Current.CancellationToken);
        accepted.Response.StatusCode.ShouldBe(200);
        acceptedBody.Reads.ShouldBeGreaterThan(0);

        using var refusedBody = new ReadProbeStream(bytes);
        var refused = await world.Host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = $"/v1/runs/{world.RunId:D}/events";
            context.Request.ContentType = "application/json";
            context.Request.Body = refusedBody;
        }, TestContext.Current.CancellationToken);
        refused.Response.StatusCode.ShouldBe(429);
        refusedBody.Reads.ShouldBe(0);
        refused.Response.Headers.RetryAfter.ToString().ShouldBe("60");
    }

    [Fact]
    public async Task Ingest_SchemaRefusal_EmitsExactlyOneRejectionMeasurement()
    {
        var clock = Clock();
        await using var world = await StartAsync(clock, permitLimit: 3);
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, Batch(world, Guid.CreateVersion7(), 1)))
            .Status.ShouldBe(HttpStatusCode.OK);
        reasons.ShouldBeEmpty();

        var refused = await IngestQaRequests.PostAsync(
            world.Client, world.RunId, new StringContent("{}", Encoding.UTF8, "application/json"));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("event_invalid");
        refused.Headers.Contains("Retry-After").ShouldBeFalse();
        reasons.ShouldHaveSingleItem().ShouldBe("event_invalid");
    }

    [Fact]
    public async Task Ingest_RateRefusal_EmitsCorrelatedRequestSpan()
    {
        var clock = Clock();
        await using var world = await StartAsync(clock);

        // Every source is listened to: the accepted batch carries its ids on the handler's own span, while a
        // refusal returns before the handler runs, so its ids can only ride the request's server span.
        using var spans = IngestTelemetryCapture.AllSpans(out var captured);
        var body = Batch(world, Guid.CreateVersion7(), sequence: 1);
        var acceptedRequest = Guid.CreateVersion7().ToString("D");
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body, acceptedRequest)).Status.ShouldBe(HttpStatusCode.OK);

        var batchSpan = await WaitForSpanAsync(captured, span => span.OperationName == "IngestEventBatch"
            && Equals(span.GetTagItem(CorrelationNames.RequestId), acceptedRequest));
        batchSpan.ShouldNotBeNull("the accepted batch's module span carries the request id");

        var refusedRequest = Guid.CreateVersion7().ToString("D");

        var refused = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body, refusedRequest);
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        var requestSpan = await WaitForSpanAsync(captured, span => span.Kind == ActivityKind.Server
            && Equals(span.GetTagItem(CorrelationNames.RequestId), refusedRequest));
        requestSpan.ShouldNotBeNull("a rate-refused request still exports its server span with the request id");
        requestSpan!.GetTagItem(CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D"));
    }

    private Task<IngestTestWorld> StartAsync(
        IngestRateLimitClock clock, int permitLimit = 1, IngestionRateLimiter? limiter = null, int slots = 1) =>
        IngestTestWorld.StartAsync(fixture, IngestTestWorld.Writer(), slots: slots,
            services: services => services.AddSingleton(limiter ?? Limiter(clock, permitLimit)), hosts: hosts);

    private static IngestRateLimitClock Clock() => new(DateTimeOffset.UnixEpoch);

    /// <summary>Waits for the first stopped span that matches; the listener reports on stop, so a short poll is enough.</summary>
    private static async Task<Activity?> WaitForSpanAsync(ConcurrentQueue<Activity> spans, Func<Activity, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (spans.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static IngestionRateLimiter Limiter(TimeProvider clock, int permitLimit, int? projectPermitLimit = null) =>
        new(
            Options.Create(new IngestionRateLimits
            {
                PermitLimit = permitLimit,
                ProjectPermitLimit = projectPermitLimit ?? permitLimit * 4,
                WindowSeconds = WindowSeconds,
            }),
            clock);

    private static object Batch(IngestTestWorld world, Guid eventId, long sequence) =>
        IngestQaRequests.Batch(IngestQaRequests.Event(eventId, IngestQaRequests.KeyOf(world, 0), sequence));

    private static async Task AssertRowsAsync(IngestTestWorld world, Guid eventId, string requestId, long expected)
    {
        (await world.CountAsync("event", $"event_id = '{eventId:D}'")).ShouldBe(expected);
        (await world.CountAsync("event_batch", $"request_id = '{requestId}'")).ShouldBe(expected);
        (await world.CountAsync("outbox", "task_type = 'usage.batch_accepted' and ("
            + $"payload -> '_evidgate_correlation' ->> 'request_id' = '{requestId}' or aggregate_id in "
            + $"(select id from public.event_batch where request_id = '{requestId}'))")).ShouldBe(expected);
    }

    private static HttpContent RefusedContent(string kind, object body)
    {
        if (kind == "gzip-invalid")
        {
            var invalid = new ByteArrayContent(Encoding.UTF8.GetBytes("not-gzip"));
            invalid.Headers.ContentType = new("application/json");
            invalid.Headers.ContentEncoding.Add("gzip");
            return invalid;
        }

        var padded = IngestQaRequests.PaddedBody(body, TransportCap + 1);
        HttpContent content = kind switch
        {
            "declared" => new StringContent(padded, Encoding.UTF8, "application/json"),
            "chunked" => new ChunkedJsonContent(Encoding.UTF8.GetBytes(padded)),
            "gzip-ratio" => Gzip(padded),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private static ByteArrayContent Gzip(string body)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(body));
        }

        var content = new ByteArrayContent(output.ToArray());
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private sealed class ReadProbeStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reads++;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
