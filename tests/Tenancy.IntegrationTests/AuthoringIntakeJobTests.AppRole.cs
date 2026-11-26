using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringIntakeJobTests
{
    /// <summary>
    /// The renewal and the terminal write lock and update the slot row, so they need the UPDATE privilege on it. The
    /// store runs as <c>evidgate_app</c>, never as the owner: without the lease-expiry grant both calls fail with 42501.
    /// </summary>
    [Fact]
    public async Task SlotLease_RenewedAndFinishedAsTheAppRole_MovesTheLeaseAndReleasesTheSlot()
    {
        var world = await SeedWorldAsync();
        var intake = await SeedDocumentAsync(world, AuthoringExtractionFixtures.Docx("Leased"));
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var scopes = api.Services.GetRequiredService<ITenantScopeFactory>();
        var store = ActivatorUtilities.CreateInstance<IntakeExtractionStore>(api.Services);

        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            using var di = api.Services.CreateScope();
            var role = await di.ServiceProvider.GetRequiredService<ITenantDb>().ScalarAsync<string>("select current_user", null, Token);
            role.ShouldBe("evidgate_app");
        }

        var claim = (await store.ClaimAsync(world.Org, intake, Token)).ShouldNotBeNull();
        await fixture.ExecuteAsync(
            world.Database,
            $"update public.authoring_intake_slot set expires_at = now() + interval '5 seconds' where intake_id = '{intake:D}'");
        var before = await LeaseSecondsAsync(world, intake);

        await store.RenewAsync(world.Org, claim, Token);

        (await LeaseSecondsAsync(world, intake)).ShouldBeGreaterThan(before + 20);

        (await store.FinishAsync(world.Org, claim, ExtractionOutcome.Refused(IntakeFailureCode.ExtractionFailed), true, Token))
            .ShouldBeTrue();
        (await StateAsync(world, intake)).ShouldBe("rejected");
        (await SlotCountAsync(world, intake)).ShouldBe(0);
    }

    private Task<double> LeaseSecondsAsync(World world, Guid intake) =>
        fixture.ScalarAsync<double>(
            world.Database,
            $"select extract(epoch from expires_at)::float8 from public.authoring_intake_slot where intake_id = '{intake:D}'");
}
