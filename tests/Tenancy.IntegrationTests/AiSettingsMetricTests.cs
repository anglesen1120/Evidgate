using System.Net;
using Evidgate.BuildingBlocks.Observability;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Every reachable outcome of the AI settings save counts exactly once under its own <c>outcome</c> value, and a save
/// that never reaches the outcome logic (an unknown project) counts nothing.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class AiSettingsMetricTests(PostgresTenancyFixture fixture)
{
    private const string Metric = EvidgateTelemetry.AiSettingsWriteMetricName;

    [Fact]
    public async Task Save_NewVersion_CountsCreatedOnce()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        using var capture = new AuthoringMetricCapture();

        var saved = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

        saved.Status.ShouldBe(HttpStatusCode.Created, saved.ErrorMessage);
        capture.Taken.ShouldBe([$"{Metric}|1|outcome=created"]);
    }

    [Fact]
    public async Task Save_IdenticalDocument_CountsNoopAndNotCreated()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var body = AiSettingsCalls.Body(AiSettingsCalls.Document());
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"")).Status.ShouldBe(HttpStatusCode.Created);
        using var capture = new AuthoringMetricCapture();

        var again = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"1\"");

        again.Status.ShouldBe(HttpStatusCode.OK, again.ErrorMessage);
        capture.Taken.ShouldBe([$"{Metric}|1|outcome=noop"]);
    }

    [Fact]
    public async Task Save_StaleIfMatch_CountsStaleAndNotCreated()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        using var capture = new AuthoringMetricCapture();

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"5\"");

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed, refused.ErrorMessage);
        capture.Taken.ShouldBe([$"{Metric}|1|outcome=stale"]);
    }

    [Fact]
    public async Task Save_OverlongField_CountsInvalid()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var document = AiSettingsCalls.Document();
        document["description"] = AiSettingsCalls.ProseOfLength(AiSettingsCalls.DescriptionLimit + 1);
        using var capture = new AuthoringMetricCapture();

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(document), "\"0\"");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        capture.Taken.ShouldBe([$"{Metric}|1|outcome=invalid"]);
    }

    [Fact]
    public async Task Save_UnknownTemplate_CountsInvalid()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var body = AiSettingsCalls.Body(AiSettingsCalls.Document(), AiSettingsCalls.Template("no-such-template", 1));
        using var capture = new AuthoringMetricCapture();

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        capture.Taken.ShouldBe([$"{Metric}|1|outcome=invalid"]);
    }

    [Fact]
    public async Task Save_UnknownProject_CountsNothing()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        using var capture = new AuthoringMetricCapture();

        var refused = await AiSettingsCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Put,
            $"/v1/projects/{Guid.CreateVersion7()}/ai-settings",
            AiSettingsCalls.Body(AiSettingsCalls.Document()),
            "\"0\"");

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        capture.Taken.ShouldBeEmpty();
    }
}
