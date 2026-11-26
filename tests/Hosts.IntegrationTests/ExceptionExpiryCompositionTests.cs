using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>The expiry sweep is wired by the Policy module itself, so a host that composes the module gets it.</summary>
public sealed class ExceptionExpiryCompositionTests
{
    [Fact]
    public async Task PolicyModule_Composed_WiresOnePlatformExpiryHandlerAndOneHourlySchedule()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();
        services.AddPolicyModule();
        await using var provider = services.BuildServiceProvider();

        var schedule = provider.GetServices<ScheduleDefinition>()
            .Where(definition => definition.Name == "waiver_and_quarantine_expiry")
            .ShouldHaveSingleItem();
        schedule.TaskType.ShouldBe("policy.expire_exceptions");
        schedule.Cron.ShouldBe("7 * * * *");
        schedule.Queue.ShouldBe(EvidgateSweeps.MaintenanceQueue);

        provider.GetServices<ITaskHandlerInvoker>()
            .Where(invoker => invoker.TaskType == schedule.TaskType)
            .ShouldHaveSingleItem()
            .RequiresTenant.ShouldBeFalse("the scan spans tenants, so the sweep runs without one");
    }
}
