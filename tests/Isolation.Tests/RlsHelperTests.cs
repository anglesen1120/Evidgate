using System.Globalization;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// AC6 / TL T2: <c>evidgate.current_org_id()</c> and
/// <c>evidgate.current_project_id()</c> are NULL-safe — unset or empty yields
/// NULL, never an error and never "every row" — and the policies read them
/// instead of <c>current_setting</c> directly.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RlsHelperTests
{
    private static readonly Guid Org = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Project = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private readonly PostgresIsolationFixture _fixture;

    public RlsHelperTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unset_Or_Empty_Yields_Null()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var app = _fixture.AppConnectionString(database);

        await using (var scope = await TenantScope.BeginAsync(app))
        {
            // Never set in this transaction.
            (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await scope.ScalarAsync<bool>("select evidgate.current_project_id() is null")).ShouldBeTrue();

            // Explicitly empty (what an endpoint that forgot the value would produce).
            await scope.SetAsync("evidgate.org_id", string.Empty);
            await scope.SetAsync("evidgate.project_id", string.Empty);
            (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await scope.ScalarAsync<bool>("select evidgate.current_project_id() is null")).ShouldBeTrue();
        }

        await using (var scope = await TenantScope.BeginAsync(app))
        {
            // Set: returned verbatim.
            await scope.SetAsync("evidgate.org_id", Org.ToString("D", CultureInfo.InvariantCulture));
            await scope.SetAsync("evidgate.project_id", Project.ToString("D", CultureInfo.InvariantCulture));
            (await scope.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(Org);
            (await scope.ScalarAsync<Guid>("select evidgate.current_project_id()")).ShouldBe(Project);
        }

        // Transaction-scoped (set_config …, true): the next transaction starts clean.
        await using (var scope = await TenantScope.BeginAsync(app))
        {
            (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        }
    }

    /// <summary>The helpers stay <c>stable</c> and the policies go through them, never through <c>current_setting</c>.</summary>
    [Fact]
    public async Task Helpers_Are_Stable_And_Policies_Use_Them()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.ColumnAsync<string>(
            database,
            """
            select p.proname || ':' || p.provolatile::text
              from pg_proc p
              join pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate' and p.proname in ('current_org_id', 'current_project_id')
             order by 1
            """))
            .ShouldBe(["current_org_id:s", "current_project_id:s"]);

        var policyPredicates = await _fixture.ColumnAsync<string>(
            database,
            """
            select coalesce(pg_get_expr(polqual, polrelid), '') || '|' || coalesce(pg_get_expr(polwithcheck, polrelid), '')
              from pg_policy
             where polrelid = 'public.rls_probe'::regclass
             order by polname
            """);

        policyPredicates.ShouldNotBeEmpty();
        policyPredicates.ShouldAllBe(predicate => !predicate.Contains("current_setting", StringComparison.Ordinal));
        policyPredicates.ShouldContain(predicate => predicate.Contains("current_org_id", StringComparison.Ordinal));

        // The migration role holds an explicit permissive policy, not a bypass:
        // without it an expand/contract backfill and every owner SECURITY DEFINER
        // sweep would silently affect zero rows (0003 header).
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select p.polcmd::text || '|' || coalesce(pg_get_expr(p.polqual, p.polrelid), '') || '|' ||
                   coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '')
              from pg_policy p
             where p.polrelid = 'public.rls_probe'::regclass
               and 'evidgate_owner'::regrole::oid = any (p.polroles)
            """))
            .ShouldBe(["*|true|true"]);

        // ... and it is the only role with an unconstrained predicate.
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select r.rolname
              from pg_policy p
              cross join lateral unnest(p.polroles) as role_oid
              join pg_roles r on r.oid = role_oid
             where p.polrelid = 'public.rls_probe'::regclass
             order by 1
            """))
            .ShouldBe(["evidgate_app", "evidgate_operator", "evidgate_owner"]);
    }
}
