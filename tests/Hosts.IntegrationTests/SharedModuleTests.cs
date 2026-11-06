using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC5 (T3.3): the Api and the Worker resolve the very same module
/// implementations — the module assemblies are shared, not copied. A connection
/// string is required for startup options but never opened.
/// </summary>
public sealed class SharedModuleTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    /// <summary>
    /// 02.2.1-T2.2 (C2 stage 4, both entry points): the Identity module's evaluator
    /// replaces the pipeline's fail-closed default wherever the module is composed, so
    /// the Worker authorizes permissions against the catalogue exactly as the Api does
    /// — one registration (<c>AddIdentityModule</c>), no per-host wiring to forget.
    /// </summary>
    [Fact]
    public async Task Same_Permission_Evaluator_In_Api_And_Worker()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        var apiEvaluator = factory.Services.GetRequiredService<IPermissionEvaluator>();

        await using var worker = await RunningWorker.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
        var workerEvaluator = worker.Services.GetRequiredService<IPermissionEvaluator>();

        Assert.IsType<PermissionEvaluator>(apiEvaluator);
        Assert.Equal(apiEvaluator.GetType().AssemblyQualifiedName, workerEvaluator.GetType().AssemblyQualifiedName);
    }

    /// <summary>
    /// 04.1.2-T4.2: the Ingestion module's producer registry is composed by both entry points through
    /// the one <c>AddIngestionModule</c> seam, so the Worker resolves the same implementation the Api
    /// does — 05's run-create runs in the Api and the Worker's task handlers share the registration.
    /// </summary>
    [Fact]
    public async Task Same_Producer_Registry_In_Api_And_Worker()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        var apiRegistry = factory.Services.GetRequiredService<IProducerRegistry>();

        await using var worker = await RunningWorker.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
        var workerRegistry = worker.Services.GetRequiredService<IProducerRegistry>();

        Assert.NotNull(apiRegistry);
        Assert.Equal(apiRegistry.GetType().AssemblyQualifiedName, workerRegistry.GetType().AssemblyQualifiedName);
    }

    /// <summary>
    /// 12.3.1-T1.3: the Usage module's ledger write path is composed by both entry points through the
    /// one <c>AddUsageModule</c> seam, so the emitters that run in the Worker (06.5.2) and in the Api
    /// (04.8.1) resolve the same <see cref="IUsageLedger"/> implementation.
    /// </summary>
    [Fact]
    public async Task Same_Usage_Ledger_In_Api_And_Worker()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        var apiLedger = factory.Services.GetRequiredService<IUsageLedger>();

        await using var worker = await RunningWorker.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
        var workerLedger = worker.Services.GetRequiredService<IUsageLedger>();

        Assert.NotNull(apiLedger);
        Assert.Equal(apiLedger.GetType().AssemblyQualifiedName, workerLedger.GetType().AssemblyQualifiedName);
    }

    /// <summary>
    /// Both entry points compose the one <c>AddUsageModule</c> seam, so the gate a hot path resolves in the
    /// Api is the gate the Worker resolves — and it is the real implementation, because an always-Ok
    /// substitute is exactly what the interface forbids.
    /// </summary>
    [Fact]
    public async Task Same_Quota_Gate_In_Api_And_Worker()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        var apiGate = factory.Services.GetRequiredService<IQuotaGate>();

        await using var worker = await RunningWorker.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
        var workerGate = worker.Services.GetRequiredService<IQuotaGate>();

        Assert.NotNull(apiGate);
        Assert.Equal(apiGate.GetType().AssemblyQualifiedName, workerGate.GetType().AssemblyQualifiedName);
    }
}
