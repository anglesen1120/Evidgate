// What each runtime role may do to a price: the app role appends its own rows only, the definer is the single
// path to the platform card, and the catalogue policy is what a tenant actually sees.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsagePriceSchemaTests
{
    [Fact]
    public async Task AppRole_IsAppendOnly_AndCannotWriteTheGlobalCard()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var id = await InsertTenantPriceAsync(database, org, "card-own");

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "UPDATE")).ShouldBeFalse(
            "a price is immutable: a correction is a new row");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "DELETE")).ShouldBeFalse(
            "a deleted price would orphan the ledger rows that name it");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", Table, "SELECT")).ShouldBeTrue(
            "the operator holds SELECT and no policy, so the grant is inert until a grant-gated form lands");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var update = await scope.CaptureErrorAsync(
                "update public.usage_price set unit_price = 0.0200 where id = @id::uuid",
                new (string Name, object Value)[] { ("id", id) });
            update.SqlState.ShouldBe("42501", "the app role holds no UPDATE on a price");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var delete = await scope.CaptureErrorAsync(
                "delete from public.usage_price where id = @id::uuid",
                new (string Name, object Value)[] { ("id", id) });
            delete.SqlState.ShouldBe("42501", "the app role holds no DELETE on a price");
        }

        // A NULL scope is the platform's row: the write policy compares the row's org to the tenant
        // setting, and NULL never equals it, so the app role is refused here rather than by a grant.
        var insert = PriceRow(Guid.CreateVersion7(), null, version: "card-global");
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var global = await scope.CaptureErrorAsync(insert.Sql, insert.Parameters);
            global.SqlState.ShouldBe("42501", "the app role cannot insert a global price directly");
            global.MessageText.ShouldContain("row-level security", Case.Sensitive);
        }
    }

    [Fact]
    public async Task GlobalInsert_DefinerWritesTheCard_AndRefusesATenantTransaction()
    {
        // Dedicated: it writes the platform card and counts it whole.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_app', "
            + "'evidgate.usage_price_insert_global(uuid,text,numeric,text,timestamptz,timestamptz,numeric,text)', 'EXECUTE')"))
            .ShouldBeTrue("the card load runs as the app role through the definer");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_operator', "
            + "'evidgate.usage_price_insert_global(uuid,text,numeric,text,timestamptz,timestamptz,numeric,text)', 'EXECUTE')"))
            .ShouldBeFalse("EXECUTE is the app role's alone among the runtime roles");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select prosecdef from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace "
            + "where n.nspname = 'evidgate' and p.proname = 'usage_price_insert_global'"))
            .ShouldBeTrue("the loader stands in for the migration role");
        // The pinned image spells the empty setting as `search_path=""`; the whole setting list is
        // compared, so an added GUC or a non-empty path fails this check as loudly as a missing one.
        (await _fixture.ScalarAsync<string>(
            database,
            "select array_to_string(proconfig, ',') from pg_catalog.pg_proc p "
            + "join pg_catalog.pg_namespace n on n.oid = p.pronamespace "
            + "where n.nspname = 'evidgate' and p.proname = 'usage_price_insert_global'"))
            .ShouldBe("search_path=\"\"", "the definer pins the empty search_path and nothing else");

        // Outside a tenant transaction the definer stores the platform row and returns its id.
        var id = Guid.CreateVersion7();
        var stored = await InsertGlobalCardAsync(database, "card-1", At(1), null, id);
        stored.ShouldBe(id);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_price where id = '{id}' and org_id is null")).ShouldBe(
            1,
            "a global price carries no org");

        // Inside one, FORCE row level security cannot tell the definer from the app role, so the
        // function carries the boundary itself and refuses before writing anything.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var refused = await scope.CaptureErrorAsync(
                DefinerSql(Guid.CreateVersion7(), "card-2", At(4), At(5)));
            refused.SqlState.ShouldBe("23514", "a global card write inside a tenant transaction is refused");
            refused.ConstraintName.ShouldBe("usage_price_global_tenant_scope");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.usage_price where org_id is null")).ShouldBe(
            1,
            "the refused call wrote nothing");
    }

    [Fact]
    public async Task TenantReads_SeeTheGlobalCardAndTheirOwnRows_NeverAnotherOrg()
    {
        // Dedicated: it writes the platform card and reads the table as one whole set.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        var globalId = await InsertGlobalCardAsync(database, "card-global", At(1), null);
        var priceA = await InsertTenantPriceAsync(database, orgA, "card-a");
        var priceB = await InsertTenantPriceAsync(database, orgB, "card-b");

        await using (var scopeA = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgA))
        {
            var visible = await scopeA.ScalarAsync<Guid[]>(
                "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.usage_price");
            visible.ShouldBe(new[] { globalId, priceA }, ignoreOrder: true);
        }

        await using (var scopeB = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgB))
        {
            var visible = await scopeB.ScalarAsync<Guid[]>(
                "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.usage_price");
            visible.ShouldBe(new[] { globalId, priceB }, ignoreOrder: true);
        }

        // No tenant setting: the catalogue half of the policy still reads the platform card, and the
        // org half matches nothing — that is the fail-closed floor of the catalogue shape.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            var visible = await unset.ScalarAsync<Guid[]>(
                "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.usage_price");
            visible.ShouldBe(new[] { globalId });
        }
    }
}
