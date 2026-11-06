using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    [Fact]
    public async Task Refusal_Trigger_Failure_Rolls_Back_The_Report_Row_And_Retry_Writes_Both()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(
            world,
            "<results><testcase name=\"probe\" /></results>"u8.ToArray());
        var outbox = Substitute.For<IOutboxWriter>();
        outbox.EnqueueAsync(
                RunReconcileContract.OutboxTaskType,
                Arg.Any<string>(),
                Arg.Any<string>(),
                world.RunId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Guid>(new InvalidOperationException("refusal-trigger-fault")));
        var handler = RefusalHandlerWithOutbox(world, outbox);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => handler.ExecuteAsync(new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, Token));

        exception.Message.ShouldBe("refusal-trigger-fault");
        (await RawReportCountAsync(world, artifactId)).ShouldBe(0);
        (await CountAsync(world, "outbox")).ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "case_attempt")).ShouldBe(0);

        var retry = await ParseAsync(world, artifactId, ProducerKind);

        retry.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        await AssertOneRefusalTriggerAsync(world, artifactId);
    }

    private static ParseRawReportHandler RefusalHandlerWithOutbox(World world, IOutboxWriter outbox)
    {
        var services = world.Host.Services;

        return new ParseRawReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IReportParserResolver>(),
            new EmulatedChildRunner(services.GetRequiredService<IParsing>()),
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            outbox,
            services.GetRequiredService<IAuditTrail>(),
            services.GetRequiredService<TimeProvider>(),
            NullLogger<ParseRawReportHandler>.Instance);
    }
}
