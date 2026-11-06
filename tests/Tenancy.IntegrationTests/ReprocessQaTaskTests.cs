using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    [Fact]
    public async Task Reprocess_CanceledRun_AppendsRevisionWithoutBillingOrChangingOriginalEvidence()
    {
        await using var world = await StartAsync();
        var (artifact, source) = await CreateOriginalRevisionAsync(world);
        var originalRevision = await RevisionHashAsync(world, 1);
        var originalReport = await ReportHashAsync(world, source);
        var producer = await EventProducerAsync(world);
        var originalEvents = await EventDigestAsync(world, producer);
        var originalBytes = await StoredObjectDigestAsync(world, artifact);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.run set state = 'canceled' where id = '{world.RunId:D}'");
        var transitions = await TransitionCountAsync(world);
        var ledger = await CountAsync(world, "usage_ledger_entry");
        var batches = await CountAsync(world, "event_batch");
        var usageOutbox = await UsageOutboxCountAsync(world);
        var parsing = await SeedRequestAsync(world, artifact, source, PinVersion);

        (await ReprocessAsync(world, parsing)).Events.ShouldBe(1);
        await ConsumeEnqueuedReconcileAsync(world, $"reprocess-{parsing:D}");

        (await RevisionCountAsync(world)).ShouldBe(2);
        (await RevisionHashAsync(world, 1)).ShouldBe(originalRevision);
        (await ReportHashAsync(world, source)).ShouldBe(originalReport);
        (await EventDigestAsync(world, producer)).ShouldBe(originalEvents);
        (await StoredObjectDigestAsync(world, artifact)).ShouldBe(originalBytes);
        (await RunStateAsync(world)).ShouldBe("canceled");
        (await TransitionCountAsync(world)).ShouldBe(transitions);
        (await CountAsync(world, "usage_ledger_entry")).ShouldBe(ledger);
        (await CountAsync(world, "event_batch")).ShouldBe(batches);
        (await UsageOutboxCountAsync(world)).ShouldBe(usageOutbox);
    }

    [Fact]
    public async Task Reprocess_MissingBytes_RetriesThenDeadLettersWithoutChangingOriginalRevision()
    {
        await using var world = await StartAsync();
        var (artifact, source) = await CreateOriginalRevisionAsync(world);
        var originalRevision = await RevisionHashAsync(world, 1);
        var originalReport = await ReportHashAsync(world, source);
        var originalEvents = await EventDigestAsync(world, await EventProducerAsync(world));
        var parsing = await SeedRequestAsync(world, artifact, source, PinVersion);
        await world.Host.Services.GetRequiredService<IObjectStorage>().DeleteAsync(ObjectKeyFor(world, artifact), Token);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ReprocessAsync(world, parsing));
        exception.Message.ShouldContain("stored object");
        await DriveMissingBytesToDeadLetterAsync(world, parsing);

        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.raw_report where id = '{parsing:D}'")).ShouldBe(0);
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await RevisionHashAsync(world, 1)).ShouldBe(originalRevision);
        (await ReportHashAsync(world, source)).ShouldBe(originalReport);
        (await EventDigestAsync(world, await EventProducerAsync(world))).ShouldBe(originalEvents);
        (await CountAsync(world, "raw_report_reprocess")).ShouldBe(1);

        await _store.PutAsync(ObjectKeyFor(world, artifact), ReportBody);
        (await ReprocessAsync(world, parsing)).Events.ShouldBe(1);
        (await RevisionHashAsync(world, 1)).ShouldBe(originalRevision);
    }

    private async Task<(Guid Artifact, Guid Source)> CreateOriginalRevisionAsync(World world)
    {
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, ReportBody);
        (await ParseOriginalReportAsync(world, artifact)).Events.ShouldBe(1);
        var source = await SourceReportIdAsync(world, artifact);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.run set state = 'queued', collection_deadline_at = now() - interval '1 minute', "
            + $"late_window_minutes = 60 where id = '{world.RunId:D}'");
        await ConsumeEnqueuedReconcileAsync(world, producerId: null);
        (await RevisionCountAsync(world)).ShouldBe(1);
        return (artifact, source);
    }

    private async Task DriveMissingBytesToDeadLetterAsync(World world, Guid parsing)
    {
        await using var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "reprocess-missing-bytes"),
            _store.Configuration(), services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.AddSingleton<IParseRunner>(factory => new EmulatedChildRunner(
                    factory.GetRequiredService<IParsing>()));
                services.AddTransactionlessTaskHandler<ReprocessReportPayload, ReprocessReportHandler>();
                services.AddSingleton<IJUnitMapper>(factory => new PinnedReleaseMapper(
                    PytestJUnitMapper.Create(factory.GetRequiredService<IParsing>())));
            });
        var scopes = host.Services.GetRequiredService<ITenantScopeFactory>();
        Guid outbox;
        await using (var scope = await scopes.BeginAsync(world.Org, cancellationToken: Token))
        {
            outbox = await host.Services.GetRequiredService<IOutboxWriter>().EnqueueAsync(
                ReprocessReportPayload.TaskType, $"{{\"parsing_id\":\"{parsing:D}\"}}", "raw_report", parsing,
                $"reprocess-missing:{parsing:D}", Token);
            await scope.CommitAsync();
        }

        // The dispatcher claims a bounded batch across tenants; under the full suite our row can
        // sit behind other worlds' rows, so keep dispatching until it has become a task.
        var task = Guid.Empty;
        for (var round = 0; round < 20 && task == Guid.Empty; round++)
        {
            await host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
            task = await _fixture.ScalarAsync<Guid>(world.Database,
                $"select coalesce((select id from public.task where idempotency_key = 'outbox:{outbox:D}'), '{Guid.Empty:D}'::uuid)");
        }

        task.ShouldNotBe(Guid.Empty);
        var queue = $"reprocess-missing-{parsing:N}";
        await _fixture.ExecuteAsync(world.Database,
            $"update public.task set queue = '{queue}', max_attempts = 2 where id = '{task:D}'");
        var consumer = host.Services.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskFailureStateAsync(world, task)).ShouldBe("retry/1");
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select last_error from public.task where id = '{task:D}'")).ShouldContain("stored object");
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(0);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.task set available_at = now() where id = '{task:D}'");

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskFailureStateAsync(world, task)).ShouldBe("dead_letter/2");
        (await _fixture.ScalarAsync<bool>(world.Database,
            $"select locked_until is null from public.task where id = '{task:D}'")).ShouldBeTrue();
    }

    private Task<string> TaskFailureStateAsync(World world, Guid task) =>
        _fixture.ScalarAsync<string>(world.Database,
            $"select state || '/' || attempts::text from public.task where id = '{task:D}'");

    private Task<long> UsageOutboxCountAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type like 'usage.%'");
}
