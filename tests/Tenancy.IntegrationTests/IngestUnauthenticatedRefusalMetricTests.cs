using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The events route counts both of its 401 refusals once under the <c>unauthenticated</c> reason and keeps the answer
/// each one gave before the counting existed: the anonymous caller's own body, the pipeline's body for a principal
/// the host resolved to no tenant. An accepted batch counts nothing.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestUnauthenticatedRefusalMetricTests(PostgresTenancyFixture fixture)
{
    private const string Unauthenticated = "unauthenticated";

    [Fact]
    public async Task EventsRoute_TenantlessPrincipal_CountsOnceAndKeepsThePipelineAnswer()
    {
        await using var world = await IngestTestWorld.StartAsync(fixture, IngestTestWorld.Writer());
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);
        using var client = world.Api.CreateClient();

        var refused = await PostAsync(client, world, headers =>
        {
            TenancyApiFactory.Resolved(user: world.User)(headers);
            headers.TryAddWithoutValidation("X-Evidgate-Org", "not-a-uuid");
        });

        refused.Status.ShouldBe(HttpStatusCode.Unauthorized);
        refused.ErrorCode.ShouldBe("unauthenticated");
        refused.ErrorMessage.ShouldBe("the request is not authenticated");
        refused.Headers.Contains("Retry-After").ShouldBeFalse();
        reasons.ToArray().ShouldBe([Unauthenticated]);
    }

    [Fact]
    public async Task EventsRoute_AnonymousCaller_StillCountsOnceAndKeepsItsOwnAnswer()
    {
        await using var world = await IngestTestWorld.StartAsync(fixture, IngestTestWorld.Writer());
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);
        using var client = world.Api.CreateClient();

        var refused = await PostAsync(client, world, _ => { });

        refused.Status.ShouldBe(HttpStatusCode.Unauthorized);
        refused.ErrorCode.ShouldBe("unauthenticated");
        refused.ErrorMessage.ShouldBe("no authenticated producer could be resolved");
        reasons.ToArray().ShouldBe([Unauthenticated]);
    }

    [Fact]
    public async Task EventsRoute_AcceptedBatch_CountsNoRejection()
    {
        await using var world = await IngestTestWorld.StartAsync(fixture, IngestTestWorld.Writer());
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);

        var accepted = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        reasons.ShouldBeEmpty();
    }

    private static Task<ApiAnswer> PostAsync(HttpClient client, IngestTestWorld world, Action<System.Net.Http.Headers.HttpRequestHeaders> caller) =>
        IngestQaRequests.PostAsync(
            client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1))),
            mutate: request => caller(request.Headers));
}
