// The operator-shaped command over the same store: a conflict is a 409 with its own code before anything is scheduled,
// so no audit row, no duplicate tally and no usage telemetry survive the failed batch.
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>RecordUsageBatch</c> over a real pipeline: the conflict is answered as <c>billing_key_conflict</c> (409) and the
/// failed Result commits nothing, so the conflicting batch leaves no ledger row, no tally and no audit row.
/// </summary>
public sealed partial class UsageLedgerPayloadHashTests
{
    [Fact]
    public void Command_DeclaresTheBillingKeyConflictCode()
    {
        RecordUsageBatchHandler.BillingKeyConflictCode.ShouldBe("billing_key_conflict");
        RecordUsageBatchHandler.BillingKeyConflictCode.ShouldNotBe(RecordUsageBatchHandler.NoOpenPeriodCode);
    }

    [Fact]
    public async Task Command_Conflict_ReturnsBillingKeyConflictAndWritesNothing()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("command");
        var stored = Entry(key, world.Project, quantity: 1.5m);
        (await RecordAsync(world, [stored])).Inserted.ShouldBe([key]);

        var result = await SendAsync(
            world,
            new RecordUsageBatch(
                [stored with { Quantity = 9.5m, Unit = "furlong" }],
                "usage-conflict-1"));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(RecordUsageBatchHandler.BillingKeyConflictCode);
        result.Error.Status.ShouldBe(409);
        result.Error.Message.ShouldBe(
            "the usage batch has 1 billing key already holding a different payload; the batch wrote nothing",
            "the message names the number of conflicting keys and nothing of the payload");

        (await LedgerCountAsync(world)).ShouldBe(1);
        (await ColumnAsync(world, key, "quantity::text")).ShouldBe("1.5");
        (await DuplicateAttemptsAsync(world)).ShouldBe(0);
        (await RollupCountAsync(world)).ShouldBe(0);
        (await CommandAuditCountAsync(world)).ShouldBe(0, "a failed command writes no audit row");
    }

    [Fact]
    public async Task Command_VerifiedReplay_StillSucceeds()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("command-replay");
        var entry = Entry(key, world.Project, quantity: 1.5m);
        (await RecordAsync(world, [entry])).Inserted.ShouldBe([key]);

        var result = await SendAsync(
            world,
            new RecordUsageBatch([entry], "usage-replay-1"));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Inserted.ShouldBeEmpty();
        result.Value.Duplicates.ShouldBe([key]);
        result.Value.Conflicts.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);
        (await DuplicateAttemptsAsync(world)).ShouldBe(1);
        (await CommandAuditCountAsync(world)).ShouldBe(0, "a batch that inserted nothing changed nothing to audit");
    }

    /// <summary>Dispatches the operator-shaped command through the real Mediator pipeline.</summary>
    private static async Task<Result<UsageBatchResult>> SendAsync(World world, RecordUsageBatch command)
    {
        var token = TestContext.Current.CancellationToken;

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, world.Project);
        using var principal = provider
            .GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(world.Project, [Permissions.Billing.Write]));

        return await provider.GetRequiredService<IMediator>().Send(command, token);
    }

    private Task<long> CommandAuditCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'usage.record_batch'");
}
