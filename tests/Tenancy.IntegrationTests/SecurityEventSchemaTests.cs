using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 11.5.1-T5.1 SQL level: migration 0053's <c>security_event</c> as the database sees it — the contract shape
/// of the table, the <c>(org_id, id)</c> composite key a later tenant FK targets, the list index the read is
/// served by, and the tenant boundary itself (ENABLE + FORCE row level security over
/// <c>evidgate.current_org_id()</c>, 00 §5.4.6 / DECISIONS F5).
/// </summary>
/// <remarks>
/// The closed <c>type</c> and <c>severity</c> vocabularies are deliberately NOT database constraints: a new type
/// is a docs decision owned elsewhere (11.1.1-T4.1, V2's <c>injection_attempt</c>), so the table carries no
/// CHECK and no enum — asserted here so a later "helpful" constraint fails this test instead of shipping. The
/// policy is declared without <c>TO</c>, so it applies to PUBLIC (<c>polroles = {0}</c>, which
/// <see cref="PostgresTenancyFixture.PoliciesAsync"/> cannot name), hence the local projection below.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class SecurityEventSchemaTests
{
    private const string Table = "security_event";

    private readonly PostgresTenancyFixture _fixture;

    public SecurityEventSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Table_DeclaresTheContractShape()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue();

        var columns = await _fixture.ColumnsAsync(database, Table);
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:NO:",
            "project_id:uuid:uuid:YES:",
            "type:text:text:NO:",
            "severity:text:text:NO:",
            "actor:jsonb:jsonb:NO:",
            "details:jsonb:jsonb:NO:",
            "request_id:text:text:YES:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            columns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"{Table} needs the 0053 contract column shape '{prefix}'");
        }

        var constraints = await _fixture.ConstraintsAsync(database, Table);

        constraints.ShouldContain(
            constraint => constraint.StartsWith("p:security_event_pkey:", StringComparison.Ordinal)
                && constraint.Contains("(id)", StringComparison.Ordinal),
            $"{Table} is addressed by its id");

        // TL T3 / 00 §5.4.6: the composite-FK target every business table declares. 0053 declares it unnamed, so
        // PostgreSQL generates the constraint name; the definition is what the rule pins.
        constraints.ShouldContain(
            constraint => constraint.StartsWith("u:", StringComparison.Ordinal)
                && constraint.Contains("UNIQUE (org_id, id)", StringComparison.Ordinal),
            $"{Table} needs unique (org_id, id) as the composite tenant key (TL T3)");

        // No CHECK and no foreign key: the V1 type/severity sets are docs decisions, and no emitter may choose the
        // project reference for an org (0053 declares neither).
        constraints.ShouldNotContain(
            constraint => constraint.StartsWith("c:", StringComparison.Ordinal),
            "0053 deliberately carries no CHECK constraint: the closed type/severity sets are docs decisions");
        constraints.ShouldNotContain(
            constraint => constraint.StartsWith("f:", StringComparison.Ordinal),
            "0053 declares no foreign key on security_event");

        var indexes = await _fixture.IndexesAsync(database, Table);
        indexes.ShouldContain(
            index => index.StartsWith("security_event_list_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id, type, severity, created_at DESC, id)", StringComparison.Ordinal),
            "the page read is served by security_event_list_idx on (org_id, project_id, type, severity, created_at DESC, id)");

        var rls = await _fixture.RowSecurityAsync(database, Table);
        rls.Enabled.ShouldBeTrue("DECISIONS F5: every business table is under ENABLE row level security");
        rls.Forced.ShouldBeTrue("DECISIONS F5: FORCE is what makes the owner obey its own policies too");

        var policies = await PolicyRowsAsync(database);
        var isolation = policies.ShouldHaveSingleItem();
        isolation.ShouldStartWith(
            "tenant_isolation:PUBLIC:*:(org_id = evidgate.current_org_id()):",
            Case.Sensitive,
            "the one policy is org_id = evidgate.current_org_id(), FOR ALL, for every role");

        // The write half: an ALL policy with no explicit WITH CHECK answers the USING expression for inserted rows,
        // so the row records no separate check ('-'). A later explicit WITH CHECK must be the same expression, or a
        // tenant could write a row it can never read back.
        var withCheck = isolation[(isolation.LastIndexOf(':') + 1)..];
        (withCheck is "-" or "(org_id = evidgate.current_org_id())")
            .ShouldBeTrue($"{Table}'s policy must not widen WITH CHECK beyond its USING expression, was '{withCheck}'");
    }

    /// <summary>
    /// A transaction without a tenant context sees no row — for the application role and, because of FORCE, for
    /// the owner too; the same statements inside a tenant scope see exactly that tenant's rows.
    /// </summary>
    [Fact]
    public async Task Query_WithoutTenantContext_ReturnsZeroRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), mine))
        {
            await seed.ExecuteAsync(
                """
                insert into public.security_event (id, org_id, type, severity, actor)
                values (@id, @org_id, 'auth_failure', 'info', '{"kind":"system"}'::jsonb)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", mine));
            await seed.CommitAsync();
        }

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), theirs))
        {
            await seed.ExecuteAsync(
                """
                insert into public.security_event (id, org_id, type, severity, actor)
                values (@id, @org_id, 'cross_tenant_probe', 'critical', '{"kind":"system"}'::jsonb)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", theirs));
            await seed.CommitAsync();
        }

        // No GUC at all: current_org_id() is NULL, so no row matches — never an error, never every row.
        await using (var anonymous = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await anonymous.ScalarAsync<long>("select count(*) from public.security_event")).ShouldBe(0);
        }

        // FORCE: the owner obeys the same policy, so its tenant-less connection sees nothing either.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            (await owner.ScalarAsync<long>("select count(*) from public.security_event")).ShouldBe(0);
        }

        // Each tenant sees its own row and only its own.
        await using (var scoped = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), mine))
        {
            (await scoped.ScalarAsync<long>("select count(*) from public.security_event")).ShouldBe(1);
            (await scoped.ScalarAsync<long>(
                    "select count(*) from public.security_event where org_id = @id",
                    ("id", theirs)))
                .ShouldBe(0);
        }
    }

    /// <summary>
    /// Every policy of the table as <c>name:role:cmd:using:withcheck</c>, naming the PUBLIC role as
    /// <c>PUBLIC</c> (a <c>TO</c>-less policy stores <c>polroles = {0}</c>, which has no <c>pg_roles</c> row).
    /// </summary>
    private Task<IReadOnlyList<string>> PolicyRowsAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            """
            select p.polname || ':' || coalesce((select r.rolname from pg_roles r where r.oid = role_oid), 'PUBLIC')
                   || ':' || p.polcmd::text || ':' ||
                   coalesce(pg_get_expr(p.polqual, p.polrelid), '-') || ':' ||
                   coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '-')
              from pg_policy p
              cross join lateral unnest(p.polroles) as role_oid
             where p.polrelid = 'public.security_event'::regclass
             order by p.polname
            """);
}
