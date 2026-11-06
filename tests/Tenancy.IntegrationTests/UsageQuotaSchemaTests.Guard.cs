using System.IO;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The counter's monotone guard: inside a period the numbers only move the way the metering path moves them.
/// The app role's column grant decides which columns it may write; the trigger decides which direction, so a
/// hand-written UPDATE cannot refund units the period already spent or lower an agreed limit, and the one
/// legitimate clear (the refusal stamp, as part of a raise) still works.
/// </summary>
public sealed partial class UsageQuotaSchemaTests
{
    private const string GuardMigrationFile = "0079_usage_quota_counter_guard.sql";

    [Fact]
    public async Task QuotaCounter_GuardRefusesLoweringUsedOrLimit()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var id = Guid.CreateVersion7();
        var app = _fixture.AppConnectionString(database);
        await ExecuteAsync(app, org, QuotaInsert(id, org, project, used: 5m, quotaLimit: 10m));

        // The positive controls: the two moves the metering path and the raise make are admitted.
        await using (var moving = await TenantScope.BeginAsync(app, org))
        {
            (await moving.ExecuteAsync($"update public.quota_counter set used = 6 where id = '{id}'")).ShouldBe(1);
            (await moving.ExecuteAsync($"update public.quota_counter set quota_limit = 12 where id = '{id}'")).ShouldBe(1);
            await moving.CommitAsync();
        }

        var loweredUsed = await CaptureErrorAsync(
            app, org, $"update public.quota_counter set used = 5 where id = '{id}'");
        loweredUsed.SqlState.ShouldBe("23514", "a period's consumed units are never refunded by an update");
        loweredUsed.MessageText.ShouldContain("used may not decrease");

        var loweredLimit = await CaptureErrorAsync(
            app, org, $"update public.quota_counter set quota_limit = 11 where id = '{id}'");
        loweredLimit.SqlState.ShouldBe("23514", "a limit moves only through the audited raise, which only raises it");
        loweredLimit.MessageText.ShouldContain("quota_limit may only move up");

        // The counter is still what the positive controls left behind: a refused statement wrote nothing.
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.quota_counter where id = '{id}' and used = 6 and quota_limit = 12"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task QuotaCounter_GuardClearsTheRefusalStampOnlyOnARaise()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var id = Guid.CreateVersion7();
        var app = _fixture.AppConnectionString(database);
        await ExecuteAsync(app, org, QuotaInsert(id, org, project, used: 10m, quotaLimit: 10m));

        await using (var blocking = await TenantScope.BeginAsync(app, org))
        {
            (await blocking.ExecuteAsync($"update public.quota_counter set blocked_at = now() where id = '{id}'"))
                .ShouldBe(1);
            await blocking.CommitAsync();
        }

        var withoutRaise = await CaptureErrorAsync(
            app, org, $"update public.quota_counter set blocked_at = null where id = '{id}'");
        withoutRaise.SqlState.ShouldBe("23514", "an unchanged limit leaves the scope blocked");
        withoutRaise.MessageText.ShouldContain("blocked_at may only clear on a raise");

        // The raise's own shape: the limit moves up in the same UPDATE that clears the stamp.
        await using (var raising = await TenantScope.BeginAsync(app, org))
        {
            (await raising.ExecuteAsync(
                $"update public.quota_counter set quota_limit = 20, blocked_at = null where id = '{id}'"))
                .ShouldBe(1);
            await raising.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.quota_counter where id = '{id}' and quota_limit = 20 and blocked_at is null"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task QuotaCounterGuard_IsInvokerScopedAndJournalled()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.ReadJournalAsync(database))
            .ShouldContain(row => row.Name == GuardMigrationFile, $"{GuardMigrationFile} must be journalled");

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_trigger where tgrelid = 'public.quota_counter'::regclass "
            + "and tgname = 'quota_counter_monotone_guard' and not tgisinternal"))
            .ShouldBe(1);

        // pg_get_functiondef() prints neither the default SECURITY INVOKER nor an implicit search_path, so the
        // two properties that make the guard safe are read from the catalogue, not from its text.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select not prosecdef from pg_catalog.pg_proc "
            + "where oid = 'evidgate.usage_quota_counter_guard()'::regprocedure"))
            .ShouldBeTrue("the guard runs with the caller's rights: no definer escalation on a hot write path");

        (await _fixture.ScalarAsync<string>(
            database,
            "select coalesce(array_to_string(proconfig, ','), '') from pg_catalog.pg_proc "
            + "where oid = 'evidgate.usage_quota_counter_guard()'::regprocedure"))
            .ShouldBe("search_path=\"\"", "an empty search_path is pinned, so nothing on the path shadows its reads");

        var definition = (await _fixture.ScalarAsync<string>(
            database, "select pg_get_functiondef('evidgate.usage_quota_counter_guard()'::regprocedure)"))
            .ShouldNotBeNull();
        definition.ShouldContain("used may not decrease");
    }

    [Fact]
    public async Task QuotaCounterGuard_MigrationRunsAgainAsANoOp()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var path = Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, GuardMigrationFile);
        var sql = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await owner.ExecuteAsync(sql);
            await owner.ExecuteAsync(sql);
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_trigger where tgrelid = 'public.quota_counter'::regclass "
            + "and tgname = 'quota_counter_monotone_guard'"))
            .ShouldBe(1, "a re-run replaces the trigger, it never stacks a second one");
    }
}
