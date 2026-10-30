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

        using var worker = await WorkerHostFactory.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
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

        using var worker = await WorkerHostFactory.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
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

        using var worker = await WorkerHostFactory.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
        var workerLedger = worker.Services.GetRequiredService<IUsageLedger>();

        Assert.NotNull(apiLedger);
        Assert.Equal(apiLedger.GetType().AssemblyQualifiedName, workerLedger.GetType().AssemblyQualifiedName);
    }

    /// <summary>
    /// 12.3.1-T1.3/T4 (review F-F): an unregistered <see cref="IQuotaGate"/> is a wiring error that fails at
    /// resolution, never "no limit row". The gate's implementation is 12.3.1-T4's, so today both hosts must throw for
    /// the missing service — the fail-closed behaviour <see cref="IQuotaGate"/> documents, and the reason
    /// <c>Ok</c> may only ever come from a gate that ran.
    /// </summary>
    [Fact]
    public async Task Usage_Quota_Gate_Unregistered_FailsClosed()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetService<IQuotaGate>());
        _ = Assert.Throws<InvalidOperationException>(() => factory.Services.GetRequiredService<IQuotaGate>());

        using var worker = await WorkerHostFactory.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(worker.Services.GetService<IQuotaGate>());
        _ = Assert.Throws<InvalidOperationException>(() => worker.Services.GetRequiredService<IQuotaGate>());
    }
}
