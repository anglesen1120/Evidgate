using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCheck_InsufficientRaise_RepeatedRefusalsPreserveStampVersionAndUsage(bool tenantCounter)
    {
        var storage = new ReportStorageSpy();
        await using var world = await StartAsync(services =>
            ReplaceObjectStorage(services, inner => storage.Wrap(inner)));
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, OneEventBody);
        var project = tenantCounter ? (Guid?)null : world.Project;
        var month = await PeriodAsync(world);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), project, month, "results", 7m, 7m, WarnRatio, Org: world.Org));
        await SeedDeferredTaskAsync(world, artifact);
        (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await RaiseAsync(counters, world, project, 7.999m)).IsSuccess.ShouldBeTrue();
        var raised = (await counters.CounterAsync(world.Org, project, month)).ShouldNotBeNull();
        raised.BlockedAt.ShouldBeNull();

        (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        var stamped = (await counters.CounterAsync(world.Org, project, month)).ShouldNotBeNull();
        stamped.BlockedAt.ShouldNotBeNull();
        stamped.Version.ShouldBe(raised.Version + 1);
        var audit = await AuditCountAsync(world);
        for (var index = 0; index < 10; index++)
        {
            (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        }

        var repeated = (await counters.CounterAsync(world.Org, project, month)).ShouldNotBeNull();
        repeated.ShouldBe(stamped);
        (await AuditCountAsync(world)).ShouldBe(audit);
        storage.Gets.ShouldBe(0);
        (await CountRawReportsAsync(world, artifact)).ShouldBe(0);
        (await RaiseAsync(counters, world, project, 8m)).IsSuccess.ShouldBeTrue();
        (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.Parsed);
        (await counters.CounterAsync(world.Org, project, month)).ShouldNotBeNull().Used.ShouldBe(8m);
        storage.Gets.ShouldBe(1);
    }

    [Fact]
    public async Task PreCheck_TransientFault_RepeatedQueueDeferralsDoNotSpendAttemptsAndRecover()
    {
        World? current = null;
        var fail = true;
        var storage = new ReportStorageSpy();
        var gate = new ReportPreCheckGate(_ =>
        {
            // A timeout of the quota read is the transient shape the probe defers on: the same statement against a
            // database that is restarting throws exactly this while the connection pool recovers.
            return fail
                ? Task.FromException(new TimeoutException("the quota read timed out"))
                : Task.CompletedTask;
        });
        await using var world = await StartAsync(services =>
        {
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
            services.AddEvidgateTasks(new ConfigurationBuilder().Build());
            services.AddTransactionlessTaskHandler<ParseRawReportPayload, ParseRawReportHandler>();
            services.AddSingleton<ParseRawReportHandler>(_ => CreateHandler(current
                ?? throw new InvalidOperationException("the report world is not ready")));
        });
        current = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, 0m, 10m);
        var queue = Unique.Slug("quota-fault");
        var task = await SeedDeferredTaskAsync(world, artifact, queue);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.task set attempts = 2 where id = '{task:D}'");
        var before = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        for (var index = 0; index < 3; index++)
        {
            (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
            (await _fixture.ScalarAsync<long>(world.Database, $"""
                select count(*) from public.task where id = '{task:D}' and state = 'retry'
                    and attempts = 2 and locked_until is null
                    and available_at > updated_at
                    and extract(epoch from available_at - updated_at) between 13 and 15
                """)).ShouldBe(1);
            await _fixture.ExecuteAsync(world.Database,
                $"update public.task set available_at = now() where id = '{task:D}'");
        }

        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().ShouldBe(before);
        storage.Gets.ShouldBe(0);
        (await CountRawReportsAsync(world, artifact)).ShouldBe(0);
        fail = false;
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select state from public.task where id = '{task:D}'")).ShouldBe("done");
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);
        storage.Gets.ShouldBe(1);
    }

    [Fact]
    public async Task PreCheck_ArtifactHasRoomButRunProjectIsFull_BlocksRunCounterUntilExactRaise()
    {
        var gate = new ReportPreCheckGate();
        var storage = new ReportStorageSpy();
        await using var world = await StartAsync(services =>
        {
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
        });
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var other = await SeedOtherProjectAsync(world);
        var artifact = await SeedArtifactAsync(world, OneEventBody, other);
        var month = await PeriodAsync(world);
        await SeedCounterAsync(counters, world, 5m, 5m);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), other, month, "results", 0m, 100m, Org: world.Org));
        var otherBefore = (await counters.CounterAsync(world.Org, other, month)).ShouldNotBeNull();
        await SeedDeferredTaskAsync(world, artifact);

        var blocked = await ParseAsync(world, artifact);

        blocked.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        blocked.QuotaRequest.ShouldNotBeNull().ProjectId.ShouldBe(world.Project);
        gate.PeekRequests.ShouldHaveSingleItem().ProjectId.ShouldBe(world.Project);
        storage.Gets.ShouldBe(0);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();
        (await counters.CounterAsync(world.Org, other, month)).ShouldNotBeNull().ShouldBe(otherBefore);
        (await RaiseAsync(counters, world, world.Project, 6m)).IsSuccess.ShouldBeTrue();
        (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.Parsed);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(6m);
        (await counters.CounterAsync(world.Org, other, month)).ShouldNotBeNull().ShouldBe(otherBefore);
    }

    [Fact]
    public async Task PreCheck_ForeignOrganizationIsFull_OnlyOwnTenantLimitCanRefuse()
    {
        await using var world = await StartAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var (foreignOrg, foreignProject) = await SeedForeignOrgAsync(world);
        var month = await PeriodAsync(world);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), null, month, "results", 10m, 10m, Org: foreignOrg));
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), foreignProject, month, "results", 10m, 10m, Org: foreignOrg));
        var foreignTenant = (await counters.CounterAsync(foreignOrg, null, month)).ShouldNotBeNull();
        var foreignCounter = (await counters.CounterAsync(foreignOrg, foreignProject, month)).ShouldNotBeNull();
        var artifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, 0m, 1m);
        await SeedDeferredTaskAsync(world, artifact);

        (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.Parsed);
        var secondArtifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedDeferredTaskAsync(world, secondArtifact);
        (await ParseAsync(world, secondArtifact)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);
        (await counters.CounterAsync(foreignOrg, null, month)).ShouldNotBeNull().ShouldBe(foreignTenant);
        (await counters.CounterAsync(foreignOrg, foreignProject, month)).ShouldNotBeNull().ShouldBe(foreignCounter);
    }
}
