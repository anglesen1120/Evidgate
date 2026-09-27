using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Policy.Application.Queries;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC5 (T3.3): the Api and the Worker resolve and run the very same Mediator
/// handler code — the module assemblies are shared, not copied. A connection
/// string is required for startup options but never opened (the slice handlers
/// don't touch the DB).
/// </summary>
public sealed class SharedModuleTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Same_Handler_In_Api_And_Worker()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        var apiHandler = factory.Services.GetRequiredService<IQueryHandler<GetDecision, DecisionResult>>();

        using var worker = await WorkerHostFactory.StartAsync(DeadConnectionString, HostLocator.FreePort(), cancellationToken: TestContext.Current.CancellationToken);
        var workerHandler = worker.Services.GetRequiredService<IQueryHandler<GetDecision, DecisionResult>>();

        Assert.Equal(apiHandler.GetType().AssemblyQualifiedName, workerHandler.GetType().AssemblyQualifiedName);

        var apiMediator = factory.Services.GetRequiredService<IMediator>();
        var workerMediator = worker.Services.GetRequiredService<IMediator>();
        const string runId = "run-shared-module-proof";

        var apiResult = await apiMediator.Send(new GetDecision(runId), TestContext.Current.CancellationToken);
        var workerResult = await workerMediator.Send(new GetDecision(runId), TestContext.Current.CancellationToken);
        Assert.Equal(apiResult, workerResult);
    }

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
}
