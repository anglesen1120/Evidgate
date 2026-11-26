using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Domain;
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AiCreditTestWorld;

namespace Tenancy.IntegrationTests;

/// <summary>The credit writer on a real database: balance, ledger row per entry, replay, refusals, rollback and serialization.</summary>
[Trait("Category", "Docker")]
public sealed class AiCreditWriterTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiCreditWriterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Grant_CreatesTheAccount_AndWritesOneEntryAndOneLedgerRow()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);

        var outcome = await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 1_000_000, "g1", "welcome"));

        outcome.Replayed.ShouldBeFalse();
        outcome.AvailableMicro.ShouldBe(1_000_000);
        (await world.CountAsync("ai_credit_entry")).ShouldBe(1);
        var ledger = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select metric || '|' || quantity::text || '|' || unit || '|' || billing_key from public.usage_ledger_entry where org_id = '{world.Org:D}'");
        ledger.ShouldBe("ai_credit|1000000|micro_credit_grant|ai:grant:g1");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select actor || '|' || reason from public.ai_credit_entry where org_id = '{world.Org:D}'")).ShouldBe($"{AuditActorTypes.System}:system|welcome");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'ai_credit.entry_appended' and object_id = '{outcome.EntryId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task EverySequenceOfKinds_LeavesTheAccountEqualToTheEntrySum_AndOneLedgerRowPerEntry()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();

        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 1_000));
        await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 400, call));
        await world.AppendedAsync(ForCall(CreditEntryKind.Consume, 300, call));
        await world.AppendedAsync(ForCall(CreditEntryKind.Refund, 100, call));
        await world.AppendedAsync(CallLess(CreditEntryKind.Expire, 50));
        var last = await world.AppendedAsync(CallLess(CreditEntryKind.Adjust, -20, reason: "correction"));

        // available: +1000 -400 +100 -50 -20; reserved: +400 -300 -100.
        (last.AvailableMicro, last.ReservedMicro).ShouldBe((630L, 0L));
        var stored = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select balance_micro || '|' || reserved_micro from public.ai_credit_account where org_id = '{world.Org:D}'");
        stored.ShouldBe("630|0");

        var summed = await _fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select coalesce(sum(case kind when 'grant' then amount_micro when 'refund' then amount_micro when 'adjust' then amount_micro
                                          when 'reserve' then -amount_micro when 'expire' then -amount_micro else 0 end), 0)
                   || '|' ||
                   coalesce(sum(case kind when 'reserve' then amount_micro when 'consume' then -amount_micro when 'refund' then -amount_micro else 0 end), 0)
              from public.ai_credit_entry where org_id = '{world.Org:D}'
            """);
        summed.ShouldBe(stored);

        (await world.CountAsync("ai_credit_entry")).ShouldBe(6);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry l join public.ai_credit_entry e on e.org_id = l.org_id and e.billing_key = l.billing_key where l.org_id = '{world.Org:D}' and l.metric = 'ai_credit'")).ShouldBe(6);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and metric = 'ai_credit' and (quantity < 0 or project_id is distinct from '{world.Project:D}'::uuid) and billing_key like 'ai:{call:D}:%'")).ShouldBe(0);
    }

    [Fact]
    public async Task Reserve_BeyondTheAvailableBalance_IsRefused_WithNoEntryAndNoLedgerRow()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 100));

        var refused = await world.AppendAsync(ForCall(CreditEntryKind.Reserve, 101, call));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(AiCreditWriter.ExhaustedCode);
        (await world.CountAsync("ai_credit_entry")).ShouldBe(1);
        (await world.CountAsync("usage_ledger_entry")).ShouldBe(1);
    }

    [Fact]
    public async Task Reserve_WithoutAnAccount_IsRefusedAsExhausted()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();

        var refused = await world.AppendAsync(ForCall(CreditEntryKind.Reserve, 1, call));

        refused.Error!.Code.ShouldBe(AiCreditWriter.ExhaustedCode);
        (await world.CountAsync("ai_credit_account")).ShouldBe(0);
    }

    [Fact]
    public async Task Replay_WithTheSamePayload_WritesNothingSecond_AndReturnsTheFirstEntry()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 500, "g"));
        var first = await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 200, call));

        var replay = await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 200, call));
        var grantReplay = await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 500, "g"));

        replay.Replayed.ShouldBeTrue();
        replay.EntryId.ShouldBe(first.EntryId);
        (replay.AvailableMicro, replay.ReservedMicro).ShouldBe((300L, 200L));
        grantReplay.Replayed.ShouldBeTrue();
        (await world.CountAsync("ai_credit_entry")).ShouldBe(2);
        (await world.CountAsync("usage_ledger_entry")).ShouldBe(2);
    }

    [Fact]
    public async Task Replay_WithADifferentPayload_IsAKeyConflict_AndChangesNothing()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 500, "g"));
        await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 200, call));

        var otherAmount = await world.AppendAsync(ForCall(CreditEntryKind.Reserve, 250, call));
        var otherGrant = await world.AppendAsync(CallLess(CreditEntryKind.Grant, 501, "g"));

        otherAmount.Error!.Code.ShouldBe(AiCreditWriter.KeyConflictCode);
        otherGrant.Error!.Code.ShouldBe(AiCreditWriter.KeyConflictCode);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select balance_micro || '|' || reserved_micro from public.ai_credit_account where org_id = '{world.Org:D}'")).ShouldBe("300|200");
        (await world.CountAsync("ai_credit_entry")).ShouldBe(2);
    }

    [Fact]
    public async Task ALedgerKeyHoldingOtherWork_RefusesTheEntry_WithoutWritingIt()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var key = AiCreditWriter.BillingKey(CallLess(CreditEntryKind.Grant, 10, "taken"));
        await world.RecordLedgerAsync(new UsageEntryInput(key, UsageMetric.AiCredit, null, 99m, "micro_credit_grant", DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow));

        var refused = await world.AppendAsync(CallLess(CreditEntryKind.Grant, 10, "taken"));

        refused.Error!.Code.ShouldBe(AiCreditWriter.KeyConflictCode);
        (await world.CountAsync("ai_credit_entry")).ShouldBe(0);
    }

    [Fact]
    public async Task ALedgerFailure_LeavesNoEntry_NoAccountChange_AndNoLedgerRow()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 100));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            world.InScopeWithLedgerAsync(
                new ThrowingLedger(),
                (writer, token) => writer.AppendAsync(world.Org, CallLess(CreditEntryKind.Grant, 50), token)));

        thrown.Message.ShouldContain("no open period");
        (await world.CountAsync("ai_credit_entry")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select balance_micro from public.ai_credit_account where org_id = '{world.Org:D}'")).ShouldBe(100);
    }

    [Fact]
    public async Task ARolledBackScope_LeavesNoEntryLedgerRowAuditRowOrAccount()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);

        var result = await world.InScopeAsync((writer, token) => writer.AppendAsync(world.Org, CallLess(CreditEntryKind.Grant, 75), token), commit: false);

        result.IsSuccess.ShouldBeTrue();
        (await world.CountAsync("ai_credit_account")).ShouldBe(0);
        (await world.CountAsync("ai_credit_entry")).ShouldBe(0);
        (await world.CountAsync("usage_ledger_entry")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'ai_credit.entry_appended'")).ShouldBe(0);
    }

    [Fact]
    public async Task ACallOfAnotherTenant_IsNotFound()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await using var other = await AiCreditTestWorld.StartAsync(_fixture);
        var foreign = await other.SeedCallAsync();
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 10));

        var refused = await world.AppendAsync(ForCall(CreditEntryKind.Reserve, 1, foreign));

        refused.Error!.Status.ShouldBe(404);
        (await world.CountAsync("ai_credit_entry")).ShouldBe(1);
    }

    [Theory]
    [InlineData((int)CreditEntryKind.Grant, 0L, "k", null)]
    [InlineData((int)CreditEntryKind.Grant, -1L, "k", null)]
    [InlineData((int)CreditEntryKind.Adjust, 0L, "k", null)]
    [InlineData((int)CreditEntryKind.Adjust, long.MinValue, "k", null)]
    [InlineData((int)CreditEntryKind.Grant, 1L, null, null)]
    [InlineData((int)CreditEntryKind.Grant, 1L, "", null)]
    [InlineData((int)CreditEntryKind.Grant, 1L, "nul\0", null)]
    [InlineData((int)CreditEntryKind.Grant, 1L, "k", "")]
    [InlineData((int)CreditEntryKind.Grant, 1L, "k", "nul\0reason")]
    public async Task InvalidRequests_AreRefusedBeforeAnyWrite(int kind, long amount, string? key, string? reason)
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);

        var refused = await world.AppendAsync(new CreditEntryRequest((CreditEntryKind)kind, amount, null, key, reason));

        refused.Error!.Status.ShouldBe(422);
        (await world.CountAsync("ai_credit_account")).ShouldBe(0);
        (await world.CountAsync("ai_credit_entry")).ShouldBe(0);
    }

    [Fact]
    public async Task CallBinding_IsEnforced_BothWays()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();

        (await world.AppendAsync(new CreditEntryRequest(CreditEntryKind.Reserve, 1, null, "k", null))).Error!.Status.ShouldBe(422);
        (await world.AppendAsync(new CreditEntryRequest(CreditEntryKind.Grant, 1, call, null, null))).Error!.Status.ShouldBe(422);
        (await world.AppendAsync(new CreditEntryRequest(CreditEntryKind.Reserve, 1, call, "k", null))).Error!.Status.ShouldBe(422);
        (await world.AppendAsync(new CreditEntryRequest(CreditEntryKind.Reserve, 1, null, null, null))).Error!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task ConcurrentReserves_SerializeOnTheAccountRow_AndNeverOverdraw()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 100));
        var calls = new List<Guid>();
        for (var index = 0; index < 20; index++)
        {
            calls.Add(await world.SeedCallAsync());
        }

        var results = await Task.WhenAll(calls.Select(call => world.AppendAsync(ForCall(CreditEntryKind.Reserve, 10, call))));

        results.Count(result => result.IsSuccess).ShouldBe(10);
        results.Where(result => !result.IsSuccess).ShouldAllBe(result => result.Error!.Code == AiCreditWriter.ExhaustedCode);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select balance_micro || '|' || reserved_micro from public.ai_credit_account where org_id = '{world.Org:D}'")).ShouldBe("0|100");
        (await world.CountAsync("ai_credit_entry")).ShouldBe(11);
    }

    [Fact]
    public async Task Balance_AtTheTopOfTheRange_RefusesAGrantInsteadOfOverflowing()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, long.MaxValue));

        var refused = await world.AppendAsync(CallLess(CreditEntryKind.Grant, 1));

        refused.IsSuccess.ShouldBeFalse();
        (await world.CountAsync("ai_credit_entry")).ShouldBe(1);
    }

    [Fact]
    public async Task Consume_AndRefund_CanOnlyDrawOnTheirOwnCallsReserve()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var a = await world.SeedCallAsync();
        var b = await world.SeedCallAsync();
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 1_000));
        await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 100, a));
        await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 100, b));

        // Call b holds reserve too, so the account-wide reserved total would cover all of these; its own reserve must not.
        (await world.AppendAsync(ForCall(CreditEntryKind.Consume, 101, a))).IsSuccess.ShouldBeFalse();
        (await world.AppendAsync(ForCall(CreditEntryKind.Consume, 60, a))).IsSuccess.ShouldBeTrue();
        (await world.AppendAsync(ForCall(CreditEntryKind.Refund, 41, a))).IsSuccess.ShouldBeFalse();
        (await world.AppendAsync(ForCall(CreditEntryKind.Refund, 40, a))).IsSuccess.ShouldBeTrue();
        (await world.AppendAsync(ForCall(CreditEntryKind.Consume, 1, await world.SeedCallAsync()))).IsSuccess.ShouldBeFalse();

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select balance_micro || '|' || reserved_micro from public.ai_credit_account where org_id = '{world.Org:D}'")).ShouldBe("840|100");
    }

    [Fact]
    public async Task Replay_BindsTheProject_AndTheExactAmount()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var big = 9_007_199_254_740_992L;
        var request = new CreditEntryRequest(CreditEntryKind.Grant, big, null, "p", null, world.Project);
        await world.AppendedAsync(request);

        (await world.AppendedAsync(request)).Replayed.ShouldBeTrue();
        (await world.AppendAsync(request with { ProjectId = null })).Error!.Code.ShouldBe(AiCreditWriter.KeyConflictCode);
        (await world.AppendAsync(request with { AmountMicro = big + 1 })).Error!.Code.ShouldBe(AiCreditWriter.KeyConflictCode);
    }

    [Fact]
    public async Task Reason_IsBoundedInCodePoints_AsTheColumnCheckCountsThem()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);

        var fits = new string('a', 250) + string.Concat(Enumerable.Repeat("\U0001F600", 250));
        (await world.AppendAsync(CallLess(CreditEntryKind.Grant, 1, reason: fits))).IsSuccess.ShouldBeTrue();
        (await world.AppendAsync(CallLess(CreditEntryKind.Grant, 1, reason: fits + "a"))).IsSuccess.ShouldBeFalse();
    }

    private sealed class ThrowingLedger : IUsageLedger
    {
        public Task<UsageBatchResult> RecordAsync(RecordUsageBatchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no open period");

        public Task<UsageEntryRecord?> FindAsync(string billingKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
