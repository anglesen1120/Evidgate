using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The role clause of the tenant policy, asserted against the migrated schema: the three tables whose
/// <c>tenant_isolation</c> policy was first declared without <c>TO</c> name <c>evidgate_app</c> alone, and
/// the audited-read role keeps no SELECT on them. A <c>TO</c>-less policy applies to PUBLIC, so
/// <c>evidgate_operator</c> — which may set its own tenant setting — could read every tenant's rows with the
/// grant alone. The three facts assert the refusal as a server error (42501), never as an empty result: a
/// policy that hides the rows would still leave the grant, and the exposure this proves against is the grant.
/// <para>
/// The policy's own reach is asserted twice more: FORCE row level security still binds the owner, and every
/// <c>tenant_isolation</c> policy of schema <c>public</c> lists exactly <c>evidgate_app</c>, so a later table
/// that reintroduces the PUBLIC form fails here by name. The five read-only facts share one migrated
/// database, created on first use (<see cref="PostgresIsolationFixture"/>); the one fact that seeds rows
/// migrates a database of its own, so a seed cannot leak into another fact.
/// </para>
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class OperatorPolicyRoleTests
{
    /// <summary>Two orgs, as the rest of the isolation suite uses; the ids are the organizations' own ids.</summary>
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    /// <summary>The tables whose policy was declared without a role clause and whose operator grant was revoked.</summary>
    private static readonly string[] TargetTables =
    [
        "public.security_event",
        "public.healing_llm_call",
        "public.healing_llm_budget",
    ];

    private readonly PostgresIsolationFixture _fixture;

    /// <summary>
    /// The one migrated database the five read-only facts share: the three refusals and the two catalogue
    /// sweeps only read the schema, so one database per fact would migrate the same head five times. Created
    /// on first use and reused afterwards, since xUnit builds a new instance per fact. Nothing is dropped
    /// here — the database belongs to <see cref="PostgresIsolationFixture"/>, which takes the whole container
    /// with it, or drops the scratch databases a reused server holds when the assembly finishes.
    /// </summary>
    private static Task<string>? _readOnlyDatabase;

    private static readonly object ReadOnlyDatabaseGate = new();

    public OperatorPolicyRoleTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    private Task<string> ReadOnlyDatabaseAsync()
    {
        lock (ReadOnlyDatabaseGate)
        {
            return _readOnlyDatabase ??= _fixture.CreateDatabaseAsync();
        }
    }

    /// <summary>Security events are tenant data: the operator's former default SELECT must answer 42501.</summary>
    [Fact]
    public async Task Operator_Select_SecurityEvent_Is_Denied()
    {
        var database = await ReadOnlyDatabaseAsync();
        await AssertOperatorSelectDeniedAsync(database, "public.security_event");
    }

    /// <summary>The LLM attempt ledger is tenant data: the operator's former explicit SELECT must answer 42501.</summary>
    [Fact]
    public async Task Operator_Select_HealingLlmCall_Is_Denied()
    {
        var database = await ReadOnlyDatabaseAsync();
        await AssertOperatorSelectDeniedAsync(database, "public.healing_llm_call");
    }

    /// <summary>Budget counters are tenant data: the operator's former explicit SELECT must answer 42501.</summary>
    [Fact]
    public async Task Operator_Select_HealingLlmBudget_Is_Denied()
    {
        var database = await ReadOnlyDatabaseAsync();
        await AssertOperatorSelectDeniedAsync(database, "public.healing_llm_budget");
    }

    /// <summary>
    /// The other half of the same policy: <c>evidgate_app</c> still reads its own org's row and never the other
    /// org's. A policy narrowed to the wrong role would fail the facts above too, so the seed is what separates
    /// "the operator lost the grant" from "nobody can read the table".
    /// </summary>
    [Fact]
    public async Task App_Tenant_Scope_Reads_Only_Own_Org()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedOneRowPerOrgAsync(database);

        foreach (var table in TargetTables)
        {
            await using (var orgA = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), OrgA))
            {
                (await orgA.ScalarAsync<int>($"select count(*)::int from {table}"))
                    .ShouldBe(1, $"{table}: the app role reads its own org's row");
                (await orgA.ScalarAsync<int>(
                        $"select count(*)::int from {table} where org_id = @org", ("org", OrgB)))
                    .ShouldBe(0, $"{table}: the app role never reads another org's row");
            }

            await using (var orgB = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), OrgB))
            {
                (await orgB.ScalarAsync<int>($"select count(*)::int from {table}"))
                    .ShouldBe(1, $"{table}: the second org reads exactly its own row");
                (await orgB.ScalarAsync<int>(
                        $"select count(*)::int from {table} where org_id = @org", ("org", OrgA)))
                    .ShouldBe(0, $"{table}: the second org never reads the first org's row");
            }
        }
    }

    /// <summary>
    /// FORCE row level security is what makes the owner obey its own policy; narrowing the tenant policy must
    /// not have been paid for by turning it off. Asserted on all three tables by name.
    /// </summary>
    [Fact]
    public async Task Force_Rls_Stays_On_Target_Tables()
    {
        var database = await ReadOnlyDatabaseAsync();
        var token = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(token);
        var tables = await RlsTableCatalog.ReadTablesAsync(connection, token);

        foreach (var name in TargetTables)
        {
            tables.ShouldContain(
                table => string.Equals(table.Name, name, StringComparison.Ordinal),
                $"{name}: the migrated schema must contain the table");

            var table = tables.Single(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
            table.RowLevelSecurityEnabled.ShouldBeTrue($"{name}: row level security must stay ENABLED");
            table.RowLevelSecurityForced.ShouldBeTrue(
                $"{name}: row level security must stay FORCED — the owner would otherwise bypass the tenant policy");
        }
    }

    /// <summary>
    /// The schema-wide form of the rule: every <c>tenant_isolation</c> policy names exactly
    /// <c>evidgate_app</c> — an exact role list, not a suffix, so a policy widened back to
    /// <c>{public, evidgate_app}</c> fails here instead of passing as "still names the app role".
    /// </summary>
    [Fact]
    public async Task Tenant_Isolation_Policies_Apply_To_App_Role_Only()
    {
        var database = await ReadOnlyDatabaseAsync();
        var token = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(token);
        var policies = (await RlsTableCatalog.ReadPoliciesAsync(connection, token))
            .Where(policy => string.Equals(policy.Name, "tenant_isolation", StringComparison.Ordinal))
            .ToList();

        policies.ShouldNotBeEmpty(
            "the catalogue yielded no tenant_isolation policy: the rule would prove nothing");

        foreach (var name in TargetTables)
        {
            policies.ShouldContain(
                policy => string.Equals(policy.Table, name, StringComparison.Ordinal),
                $"{name} must carry a tenant_isolation policy");
        }

        foreach (var policy in policies)
        {
            policy.Roles.SequenceEqual([RlsTableCatalog.ApplicationRole]).ShouldBeTrue(
                $"{policy.Table}: tenant_isolation must name evidgate_app alone, was [{string.Join(", ", policy.Roles)}] "
                + "(a TO-less policy lists the PUBLIC pseudo-role as public)");
        }
    }

    /// <summary>
    /// Proves the grant is gone, not merely that a policy hides the rows: the operator sets a foreign tenant
    /// and selects, and the server answers permission denied (42501). An empty result would mean the policy
    /// works while the grant — the thing that let the role reach every tenant — is still there.
    /// </summary>
    private async Task AssertOperatorSelectDeniedAsync(string database, string table)
    {
        (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_table_privilege('evidgate_operator', '{table}', 'select')"))
            .ShouldBeFalse($"{table}: the operator must hold no SELECT grant for the policy to be the only gate");

        await using var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database), OrgB);
        var error = await scope.CaptureErrorAsync($"select count(*)::int from {table}");

        error.SqlState.ShouldBe(
            PostgresErrorCodes.InsufficientPrivilege,
            $"{table}: the operator's select must be refused, not merely return no rows ({error.MessageText})");
        error.MessageText.ShouldContain("permission denied");
    }

    /// <summary>
    /// One committed row per org, written on the owner connection: the owner backfill policy is what admits a
    /// cross-tenant fixture write under FORCE, and the app-role reads that follow run in their own
    /// transactions, so the seed has to be committed to be visible to them.
    /// </summary>
    private async Task SeedOneRowPerOrgAsync(string database)
    {
        var token = TestContext.Current.CancellationToken;

        await using (var catalogue = new NpgsqlConnection(_fixture.OwnerConnectionString(database)))
        {
            await catalogue.OpenAsync(token);
            var schema = await TenantSchema.ReadAsync(catalogue, null, token);

            await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
            var factory = new TenantRowFactory(schema, (sql, parameters) => owner.ExecuteAsync(sql, parameters));

            foreach (var table in TargetTables)
            {
                await factory.CreateAsync(table, OrgA, token);
                await factory.CreateAsync(table, OrgB, token);
            }

            await owner.CommitAsync();
        }
    }
}
