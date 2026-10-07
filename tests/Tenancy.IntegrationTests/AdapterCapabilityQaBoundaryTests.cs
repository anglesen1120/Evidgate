using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Ingestion.Application.Commands;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AdapterCapabilityQaBoundaryTests(PostgresTenancyFixture fixture)
{
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, int.MaxValue, true)]
    [InlineData(int.MaxValue, 0, false)]
    public async Task CaptureControlFalseAndInt32Boundaries_AreNegotiated(int major, int minor, bool accepted)
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await CreateOrgAsync(client, Guid.CreateVersion7(), "qa-boundary");
        await using var scope = api.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(RequestPrincipal.Service(null, [Permissions.Event.Write]));
        var document = $$"""
            {"protocol":{"major":{{major}},"minor":{{minor}}},"engine":{"family":"playwright","version":"1.49.0"},
             "platforms":["linux-x64"],"supports":{"discovery":true,"step_events":true,"retry":true,"cancel":true,"capture_control":false,"unknown_future":false},"mode":"full"}
            """;
        // Exactly the 16 KiB raw-body cap, not an approximate max-size probe.
        document += new string(' ', 16384 - Encoding.UTF8.GetByteCount(document));
        var result = await provider.GetRequiredService<IMediator>().Send(new RegisterAdapterCapability("playwright", Encoding.UTF8.GetBytes(document)), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBe(accepted);
        if (accepted)
        {
            (await fixture.ScalarAsync<int>(database, "select protocol_minor from public.adapter_capability")).ShouldBe(minor);
            (await fixture.ScalarAsync<bool>(database, "select (supports ->> 'capture_control')::boolean from public.adapter_capability")).ShouldBeFalse();
            (await fixture.ScalarAsync<bool>(database, "select (supports ->> 'unknown_future')::boolean from public.adapter_capability")).ShouldBeFalse();
        }
        else
        {
            result.Error!.Code.ShouldBe(ApiErrorCode.UnsupportedProtocolMajor);
            result.Error.Status.ShouldBe(422);
            result.Error.Retryable.ShouldBeFalse();
            result.Error.Details!.ShouldContain(detail => detail.Reason == "adapter_major=2147483647");
            (await fixture.ScalarAsync<long>(database, "select count(*) from public.adapter_capability")).ShouldBe(0);
        }
    }
}
