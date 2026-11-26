using Evidgate.Modules.Healing.Domain;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AiCreditTestWorld;

namespace Tenancy.IntegrationTests;

/// <summary>The database side of the credit tables: append-only entries, grants, tenant isolation, checks and purge order.</summary>
[Trait("Category", "Docker")]
public sealed class AiCreditSchemaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiCreditSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AiCreditEntry_UpdateDelete_Rejected()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var entry = await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 10));
        var id = entry.EntryId.ToString("D");

        // The app role lacks the privilege; the trigger then refuses the owner too, and a delete by anyone but the owner.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"update public.ai_credit_entry set amount_micro = 99 where id = '{id}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"delete from public.ai_credit_entry where id = '{id}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org))
        {
            (await owner.CaptureErrorAsync($"update public.ai_credit_entry set reason = 'edit' where id = '{id}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using (var worker = await TenantScope.BeginAsync(_fixture.WorkerConnectionString(world.Database), world.Org))
        {
            (await worker.CaptureErrorAsync($"delete from public.ai_credit_entry where id = '{id}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await world.CountAsync("ai_credit_entry")).ShouldBe(1);
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "ai_credit_entry", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "ai_credit_entry", "DELETE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "ai_credit_account", "DELETE")).ShouldBeFalse();
    }

    [Fact]
    public async Task AppRole_CanMoveOnlyTheBalanceColumnsOfAnAccount()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 10));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.CaptureErrorAsync($"update public.ai_credit_account set org_id = org_id, created_at = now() where org_id = '{world.Org:D}'"))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Tenants_SeeOnlyTheirOwnRows_AndCannotForgeAnEntryIntoAnother()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await using var other = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 10));
        var account = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.ai_credit_account where org_id = '{world.Org:D}'");
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, other.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.ai_credit_entry")).ShouldBe(0);
            (await scope.ScalarAsync<long>("select count(*) from public.ai_credit_account")).ShouldBe(0);
        }

        // Another tenant's scope naming this tenant's org id breaks the row policy; naming its own org but this account breaks the key.
        await using (var scope = await TenantScope.BeginAsync(app, other.Org))
        {
            (await scope.CaptureErrorAsync(InsertGrant(world.Org, account))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var scope = await TenantScope.BeginAsync(app, other.Org))
        {
            (await scope.CaptureErrorAsync(InsertGrant(other.Org, account))).SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }
    }

    [Theory]
    [InlineData("'grant', 0, null", PostgresErrorCodes.CheckViolation)]
    [InlineData("'grant', -5, null", PostgresErrorCodes.CheckViolation)]
    [InlineData("'adjust', 0, null", PostgresErrorCodes.CheckViolation)]
    [InlineData("'reserve', 5, null", PostgresErrorCodes.CheckViolation)]
    [InlineData("'bogus', 5, null", PostgresErrorCodes.CheckViolation)]
    public async Task Entry_Checks_RefuseBadShapes(string kindAmountCall, string sqlState)
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 10));
        var account = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.ai_credit_account where org_id = '{world.Org:D}'");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.CaptureErrorAsync(InsertWith(world.Org, account, kindAmountCall, "sha256:" + new string('a', 64)))).SqlState.ShouldBe(sqlState);
    }

    [Fact]
    public async Task Entry_Checks_RefuseABadHash_AndACallOnAGrant()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 10));
        var account = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.ai_credit_account where org_id = '{world.Org:D}'");
        var call = await world.SeedCallAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(InsertWith(world.Org, account, "'grant', 5, null", "md5:abc"))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(InsertWith(world.Org, account, $"'grant', 5, '{call:D}'", "sha256:" + new string('a', 64))))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
    }

    [Fact]
    public async Task ACallCarriesOneEntryOfEachKind_AndPurgeMustRemoveEntriesFirst()
    {
        await using var world = await AiCreditTestWorld.StartAsync(_fixture);
        var call = await world.SeedCallAsync();
        await world.AppendedAsync(CallLess(CreditEntryKind.Grant, 100));
        await world.AppendedAsync(ForCall(CreditEntryKind.Reserve, 10, call));
        var account = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.ai_credit_account where org_id = '{world.Org:D}'");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var sql = InsertWith(world.Org, account, $"'reserve', 5, '{call:D}'", "sha256:" + new string('b', 64))
                .Replace("'k-", "'other-", StringComparison.Ordinal);
            (await scope.CaptureErrorAsync(sql)).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }

        var purge = await Should.ThrowAsync<PostgresException>(() =>
            _fixture.ExecuteAsync(world.Database, $"delete from public.healing_llm_call where id = '{call:D}'"));
        purge.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    private static string InsertGrant(Guid org, Guid account) =>
        InsertWith(org, account, "'grant', 5, null", "sha256:" + new string('a', 64));

    private static string InsertWith(Guid org, Guid account, string kindAmountCall, string hash) =>
        $"""
        insert into public.ai_credit_entry (id, org_id, account_id, kind, amount_micro, call_id, billing_key, payload_hash, actor)
        values ('{Guid.NewGuid():D}', '{org:D}', '{account:D}', {kindAmountCall.Split(',')[0]}, {kindAmountCall.Split(',')[1]},
                {kindAmountCall.Split(',')[2]}, 'k-{Guid.NewGuid():N}', '{hash}', 'test')
        """;
}
