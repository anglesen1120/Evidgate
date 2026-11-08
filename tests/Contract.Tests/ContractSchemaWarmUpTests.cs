using Evidgate.BuildingBlocks.Contracts.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The schema set is compiled when the host starts, not inside the first body-validating request
/// (a first load is a ~55 s Roslyn compile that would otherwise hold a pooled connection in an open transaction).
/// </summary>
public sealed class ContractSchemaWarmUpTests
{
    [Fact]
    public async Task StartingTheApiHost_BuildsTheSchemaSetWithoutAnyRequest()
    {
        await using var factory = new EvidgateApiFactory();
        _ = factory.Server;

        var warmUp = factory.Services.GetServices<IHostedService>().OfType<ContractSchemaWarmUpService>().ShouldHaveSingleItem();
        await warmUp.Completion.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        warmUp.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task TheWarmUp_ResolvesTheRegisteredSetExactlyOnce_AndLaterResolvesReuseIt()
    {
        var loads = 0;
        var services = new ServiceCollection();
        services.AddSingleton(_ =>
        {
            Interlocked.Increment(ref loads);
            return ContractSchemaSet.Load(ContractPaths.ContractsDirectory);
        });
        var provider = services.BuildServiceProvider();
        var warmUp = new ContractSchemaWarmUpService(provider, NullLogger<ContractSchemaWarmUpService>.Instance);

        loads.ShouldBe(0);
        await warmUp.StartAsync(TestContext.Current.CancellationToken);
        await warmUp.Completion;

        loads.ShouldBe(1);
        provider.GetRequiredService<ContractSchemaSet>().Keys.ShouldNotBeEmpty();
        loads.ShouldBe(1);
    }

    [Fact]
    public async Task ARequestResolvingWhileTheWarmUpRuns_ReusesTheSameSet_WithoutASecondLoad()
    {
        var loads = 0;
        using var inFlight = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var services = new ServiceCollection();
        services.AddSingleton(_ =>
        {
            Interlocked.Increment(ref loads);
            inFlight.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return ContractSchemaSet.Load(ContractPaths.ContractsDirectory);
        });
        var provider = services.BuildServiceProvider();
        var warmUp = new ContractSchemaWarmUpService(provider, NullLogger<ContractSchemaWarmUpService>.Instance);

        await warmUp.StartAsync(TestContext.Current.CancellationToken);
        inFlight.Wait(TestContext.Current.CancellationToken);
        var request = Task.Run(() => provider.GetRequiredService<ContractSchemaSet>(), TestContext.Current.CancellationToken);
        release.Set();
        var fromRequest = await request;
        await warmUp.Completion;

        loads.ShouldBe(1);
        provider.GetRequiredService<ContractSchemaSet>().ShouldBeSameAs(fromRequest);
    }

    [Fact]
    public async Task AMissingContractsDirectory_FaultsTheWarmUp_AndTheRequestPathFailsTheSameWay()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-contracts-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddSingleton(_ => ContractSchemaSet.Load(missing));
        var provider = services.BuildServiceProvider();
        var warmUp = new ContractSchemaWarmUpService(provider, NullLogger<ContractSchemaWarmUpService>.Instance);

        await warmUp.StartAsync(TestContext.Current.CancellationToken);

#pragma warning disable VSTHRD003 // The completion source belongs to the service under test.
        var warmUpError = await Should.ThrowAsync<Exception>(() => warmUp.Completion);
#pragma warning restore VSTHRD003
        var requestError = Should.Throw<Exception>(() => provider.GetRequiredService<ContractSchemaSet>());
        requestError.GetType().ShouldBe(warmUpError.GetType());
        requestError.Message.ShouldBe(warmUpError.Message);
        warmUpError.Message.ShouldContain(missing);
    }
}
