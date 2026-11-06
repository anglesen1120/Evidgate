using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    private static readonly JsonSerializerOptions RefusalPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Theory]
    [InlineData(ProducerKind)]
    [InlineData(UnknownKind)]
    public async Task Refusal_Trigger_At_The_Collection_Deadline_Persists_Incomplete_With_Missing_Result(string producerKind)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(
            world,
            "<results><testcase name=\"probe\" /></results>"u8.ToArray());
        await ParseAsync(world, artifactId, producerKind);
        var json = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload::text from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'");
        var payload = JsonSerializer.Deserialize<RunReconcilePayload>(json, RefusalPayloadOptions);
        payload.ShouldNotBeNull();

        await ConsumeRefusalTriggerAsync(world, payload);

        (await CountAsync(world, "evidence_revision")).ShouldBe(0, "before the deadline the run is not collectable");
        (await RunStateAsync(world)).ShouldBe("queued");
        await SetRunStateAsync(world, "update public.run set collection_deadline_at = now() - interval '1 minute'");

        await ConsumeRefusalTriggerAsync(world, payload);

        (await RunStateAsync(world)).ShouldBe("incomplete");
        (await CountAsync(world, "evidence_revision")).ShouldBe(1);
        var revision = await _fixture.ScalarAsync<string>(
            world.Database,
            "select completeness || '|' || array_to_string(missing, ',') from public.evidence_revision "
            + $"where org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'");
        revision.ShouldContain("incomplete|");
        revision.ShouldContain(ReconcileMissingCodes.MissingResult);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "case_attempt")).ShouldBe(0);
    }

    private static async Task ConsumeRefusalTriggerAsync(World world, RunReconcilePayload payload)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var handler = new ReconcileRunTaskHandler(
            services.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReconcileRunTaskHandler>.Instance);

        await handler.HandleAsync(payload, scope, Token);
        await scope.CommitAsync(Token);
    }
}
