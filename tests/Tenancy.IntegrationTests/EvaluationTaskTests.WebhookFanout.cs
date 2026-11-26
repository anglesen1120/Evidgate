using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application.Evaluation;
using Evidgate.Modules.Policy.Webhooks.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// Who is told about a live evaluation: the outbox row only a live verdict writes, and the fan-out that turns one such
// row into exactly one delivery (and one task) per active subscribed endpoint, however often the row is redelivered.
public sealed partial class EvaluationTaskTests
{
    /// <summary>
    /// A live verdict writes one evaluation.created outbox row naming the evaluation; a shadow verdict beside it, and the
    /// verdict of a replay run, write none.
    /// </summary>
    [Fact]
    public async Task Evaluate_LiveEmitsEvaluationCreated_ReplayAndShadowDoNot()
    {
        await using var web = await StartWebhookWorldAsync();
        var seed = await SeedAsync(web.World);

        var live = await EvaluateAsync(web.World, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        (await CountRowsAsync(web, "outbox", "task_type = 'evaluation.created'")).ShouldBe(1);
        var payload = await OutboxPayloadAsync(web, live.Id);
        payload.EvaluationId.ShouldBe(live.Id);
        payload.ProjectId.ShouldBe(seed.Project);

        var shadow = await EvaluateAsync(web.World, seed, EvaluationMode.Shadow, TestContext.Current.CancellationToken);

        shadow.Mode.ShouldBe("shadow");
        (await CountRowsAsync(web, "outbox", "task_type = 'evaluation.created'")).ShouldBe(1, "a shadow opinion tells nobody");

        await using var replayWorld = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var replaySeed = await SeedAsync(replayWorld);
        await MarkReplayAsync(replayWorld, replaySeed);

        var replayed = await EvaluateAsync(replayWorld, replaySeed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        replayed.Mode.ShouldBe("shadow", "a replay run stores a shadow row whatever slot was asked for");
        var replayOutbox = await _fixture.ScalarAsync<long>(
            replayWorld.Database,
            $"select count(*) from public.outbox where org_id = '{replayWorld.Org:D}' and task_type = 'evaluation.created'");
        replayOutbox.ShouldBe(0, "a replay is advice about old evidence, not the run's verdict");
    }

    /// <summary>
    /// The same outbox message handled twice (a redelivered task) leaves one row and one task per endpoint, and a second
    /// endpoint gets its own row: the key is (org, endpoint, event).
    /// </summary>
    [Fact]
    public async Task DeliveryDedupe_PerEndpoint()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (first, _) = await SeedEndpointAsync(web);
        var (second, _) = await SeedEndpointAsync(web);

        await FanOutAsync(web, evaluation);

        (await DeliveryCountAsync(web, first)).ShouldBe(1);
        (await DeliveryCountAsync(web, second)).ShouldBe(1);

        var duplicate = await Should.ThrowAsync<PostgresException>(() => SeedDeliveryAsync(web, first, evaluation));
        duplicate.SqlState.ShouldBe("23505", "one delivery per endpoint and event, enforced by the table");
    }

    /// <summary>
    /// A redelivered evaluation.created outbox message creates no second row and no second webhook.deliver task for any
    /// endpoint.
    /// </summary>
    [Fact]
    public async Task FanOut_RedeliveredOutboxMessage_NoSecondRowOrTask()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        await SeedEndpointAsync(web);
        await SeedEndpointAsync(web);

        await FanOutAsync(web, evaluation);
        await FanOutAsync(web, evaluation);
        await FanOutAsync(web, evaluation);

        (await CountRowsAsync(web, "webhook_outbound_delivery", $"event_id = '{evaluation:D}'")).ShouldBe(2);
        (await CountRowsAsync(web, "task", "task_type = 'webhook.deliver'")).ShouldBe(2, "one task per new row, none for a redelivery");
    }

    /// <summary>A disabled or deleted endpoint is not fanned out to: only the active one gets a row.</summary>
    [Fact]
    public async Task FanOut_DisabledOrDeletedEndpoint_GetsNoRow()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (active, _) = await SeedEndpointAsync(web);
        var (disabled, _) = await SeedEndpointAsync(web, state: "disabled");
        var (deleted, _) = await SeedEndpointAsync(web, state: "deleted");

        await FanOutAsync(web, evaluation);

        (await DeliveryCountAsync(web, active)).ShouldBe(1);
        (await DeliveryCountAsync(web, disabled)).ShouldBe(0);
        (await DeliveryCountAsync(web, deleted)).ShouldBe(0);
        (await CountRowsAsync(web, "task", "task_type = 'webhook.deliver'")).ShouldBe(1);
    }

    /// <summary>The handler a worker would run for the evaluation's real outbox row, on its own committed tenant scope.</summary>
    private async Task FanOutAsync(WebhookWorld web, Guid evaluation)
    {
        var payload = await OutboxPayloadAsync(web, evaluation);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await web.World.Api.Services.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(web.World.Org, cancellationToken: cancellationToken);

        await web.World.Api.Services.GetRequiredService<WebhookFanoutHandler>()
            .HandleAsync(payload, scope, cancellationToken);

        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>The payload of the outbox row the evaluation wrote, read as the owner.</summary>
    private async Task<WebhookFanoutPayload> OutboxPayloadAsync(WebhookWorld web, Guid evaluation)
    {
        var json = await _fixture.ScalarAsync<string>(
            web.World.Database,
            $"""
            select payload::text from public.outbox
             where org_id = '{web.World.Org:D}' and task_type = 'evaluation.created' and aggregate_id = '{evaluation:D}'
            """);
        using var document = JsonDocument.Parse(json);

        return new WebhookFanoutPayload(
            document.RootElement.GetProperty("evaluation_id").GetGuid(),
            document.RootElement.GetProperty("project_id").GetGuid());
    }

    private Task<long> DeliveryCountAsync(WebhookWorld web, Guid endpoint) =>
        CountRowsAsync(web, "webhook_outbound_delivery", $"endpoint_id = '{endpoint:D}'");
}
