using Evidgate.BuildingBlocks.Persistence.Isolation;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC1 / AC5 / 00 §5.4.1, §5.4.3, §5.4.7 (DECISIONS F5; TL T2, T3, T4): every table
/// that <c>contracts/rls-exceptions.txt</c> does not excuse runs under
/// <c>ENABLE</c> + <c>FORCE ROW LEVEL SECURITY</c> with the policy shape its kind
/// requires, and the boot self-check that enforces the same contract names a table
/// that breaks it.
/// <para>
/// The coverage assertion below reads the catalogue with its own SQL on purpose: it
/// is the independent half of the pair, so a rule bug in the production check cannot
/// make the test pass. The scratch-table case runs the production check, because
/// there the question is the reverse — that the code which decides the boot really
/// refuses a table it should.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RlsPolicyCoverageTests
{
    // The two accepted predicates, written the way a migration writes them; every
    // expression is judged by the production matcher, so the assertion is about the
    // predicate and not about how pg_get_expr parenthesizes it. Written out here rather
    // than imported from the production code on purpose: the predicates and the catalogue
    // read are the independent half of the pair (the matcher is shared — see Matches).
    private const string TenantPredicate = "org_id = evidgate.current_org_id()";
    private const string CataloguePredicate = "org_id IS NULL OR org_id = evidgate.current_org_id()";

    private readonly PostgresTenancyFixture _fixture;

    public RlsPolicyCoverageTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RlsPolicies_EveryBusinessTable_HasForcedRls()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var exceptions = RlsExceptionFile.Read();

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var tables = await ReadTablesAsync(connection);
        var policies = await ReadPoliciesAsync(connection);

        var checkedTables = new List<string>();
        var skippedTables = new List<string>();
        var failures = new List<string>();

        foreach (var table in tables)
        {
            var entry = exceptions.Find(table.Name);

            // A mechanism table is outside the rule set entirely (00 §5.3, 0006-0008):
            // it carries org_id and holds no policy, because the dispatcher scan and
            // the task claim walk every org by design.
            if (entry?.Kind == RlsExceptionKind.Mechanism)
            {
                skippedTables.Add(table.Name);
                continue;
            }

            checkedTables.Add(table.Name);

            if (!table.RowLevelSecurityEnabled)
            {
                failures.Add($"{table.Name}: row level security is not ENABLED (DECISIONS F5)");
            }

            if (!table.RowLevelSecurityForced)
            {
                failures.Add($"{table.Name}: row level security is not FORCED — the owner would bypass its policies (DECISIONS F5)");
            }

            var tablePolicies = policies.Where(policy => string.Equals(policy.Table, table.Name, StringComparison.Ordinal)).ToList();

            if (tablePolicies.Count == 0)
            {
                failures.Add($"{table.Name}: no policy exists");
                continue;
            }

            if (entry?.Kind == RlsExceptionKind.Catalogue
                && !tablePolicies.Any(policy => Matches(policy, CataloguePredicate)))
            {
                failures.Add(
                    $"{table.Name}: a catalogue table needs a policy that reads the built-in rows for every org "
                    + "('org_id IS NULL OR org_id = evidgate.current_org_id()', 02 §4.5)");
            }

            if (entry is null
                && table.HasOrgId
                && !tablePolicies.Any(policy => Matches(policy, TenantPredicate)))
            {
                failures.Add(
                    $"{table.Name}: an org-scoped table needs a policy that scopes it through "
                    + "evidgate.current_org_id() (00 §5.4.7/T2)");
            }
        }

        failures.ShouldBeEmpty();

        // The scan is not vacuous: the M1 tenant tables were judged, and the three
        // mechanism tables were skipped — which is also how this test proves the
        // exception list, not a hard-coded name, decides who is exempt.
        checkedTables.Count.ShouldBeGreaterThanOrEqualTo(4);
        checkedTables.ShouldContain("public.organization");
        checkedTables.ShouldContain("public.project");
        checkedTables.ShouldContain("public.rls_probe");
        checkedTables.ShouldContain("public.idempotency_record");
        skippedTables.ShouldBe(["public.outbox", "public.schema_migration", "public.task"]);
    }

    [Fact]
    public async Task SelfCheck_NamesScratchTableWithoutRls_AndAcceptsTheFix()
    {
        var database = await _fixture.CreateDatabaseAsync();

        await using var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var check = new IsolationSelfCheck(dataSource, RlsExceptionFile.Read());

        // Baseline: the migrated schema of this build passes, so the refusal below is
        // caused by the scratch table and not by a check that is red by construction.
        var baseline = await check.RunAsync(TestContext.Current.CancellationToken);
        baseline.Passed.ShouldBeTrue(string.Join("; ", baseline.Violations));

        // A public table with no row level security at all — what a forgotten copy of
        // the 0003 template looks like on a live database, after the CI lint passed.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await owner.ExecuteAsync(
                """
                create table public.scratch_leak (
                  org_id uuid not null,
                  id uuid not null,
                  label text not null,
                  constraint scratch_leak_pkey primary key (org_id, id));
                """);
            await owner.CommitAsync();
        }

        var flagged = await check.RunAsync(TestContext.Current.CancellationToken);

        flagged.Passed.ShouldBeFalse();
        flagged.CheckedTables.ShouldContain("public.scratch_leak");
        flagged.Violations.ShouldContain(violation =>
            violation.Table == "public.scratch_leak"
            && violation.Code == IsolationSelfCheckCodes.RowLevelSecurityNotEnabled);
        flagged.Violations.ShouldContain(violation =>
            violation.Table == "public.scratch_leak"
            && violation.Code == IsolationSelfCheckCodes.RowLevelSecurityNotForced);
        flagged.Violations.ShouldContain(violation =>
            violation.Table == "public.scratch_leak"
            && violation.Code == IsolationSelfCheckCodes.NoPolicy);

        // The boot refusal the API and the Worker speak: one named code, table names,
        // and nothing that could carry a credential (00 §5.7 S3).
        var refusal = new IsolationSelfCheckException(flagged);
        refusal.Message.ShouldStartWith(IsolationSelfCheckCodes.SelfCheckFailed);
        refusal.Message.ShouldContain("public.scratch_leak");
        refusal.Message.ShouldContain(IsolationSelfCheckCodes.NoPolicy);
        refusal.Report.Violations.ShouldBe(flagged.Violations);

        // The fix — the copy block of 0003 — clears it, so a compliant table is not
        // rejected either.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await owner.ExecuteAsync(
                """
                alter table public.scratch_leak enable row level security;
                alter table public.scratch_leak force row level security;
                create policy scratch_leak_app_all on public.scratch_leak
                  for all
                  to evidgate_app
                  using (org_id = evidgate.current_org_id())
                  with check (org_id = evidgate.current_org_id());
                """);
            await owner.CommitAsync();
        }

        var repaired = await check.RunAsync(TestContext.Current.CancellationToken);
        repaired.Passed.ShouldBeTrue(string.Join("; ", repaired.Violations));
    }

    /// <summary>
    /// The list the published process carries is the file this commit contains —
    /// the embedded resource is a build-time copy of
    /// <c>contracts/rls-exceptions.txt</c>, and the CI lint of 02.6.1-T2 reads the
    /// file. Divergence would mean the lint and the running app disagree about which
    /// tables are exempt, in whichever direction the edit went.
    /// </summary>
    [Fact]
    public void EmbeddedExceptionList_IsTheRepositoryFile()
    {
        var embedded = RlsExceptionList.LoadEmbedded();
        var fromDisk = RlsExceptionFile.Read();

        embedded.Entries.ShouldBe(fromDisk.Entries);
        embedded.Entries.ShouldNotBeEmpty();

        embedded.KindOf("public.organization").ShouldBe(RlsExceptionKind.Platform);
        embedded.KindOf("public.role").ShouldBe(RlsExceptionKind.Catalogue);
        embedded.KindOf("public.role_permission").ShouldBe(RlsExceptionKind.Catalogue);
        embedded.KindOf("public.outbox").ShouldBe(RlsExceptionKind.Mechanism);
        embedded.KindOf("public.task").ShouldBe(RlsExceptionKind.Mechanism);
        embedded.KindOf("public.schema_migration").ShouldBe(RlsExceptionKind.Mechanism);

        // Documented as deliberately absent: both are org-scoped, carry
        // `unique (org_id, id)` and run under FORCE ROW LEVEL SECURITY with the
        // standard tenant policy, so excusing them would hide a real regression.
        embedded.KindOf("public.idempotency_record").ShouldBeNull();
        embedded.KindOf("public.rls_probe").ShouldBeNull();
    }

    private static async Task<List<TableRow>> ReadTablesAsync(NpgsqlConnection connection)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   exists (select 1
                             from pg_catalog.pg_attribute a
                            where a.attrelid = c.oid
                              and a.attname = 'org_id'
                              and a.attnum > 0
                              and not a.attisdropped)
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var tables = new List<TableRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(new TableRow(
                await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<bool>(1, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<bool>(2, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<bool>(3, TestContext.Current.CancellationToken)));
        }

        return tables;
    }

    private static async Task<List<PolicyRow>> ReadPoliciesAsync(NpgsqlConnection connection)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   coalesce(pg_catalog.pg_get_expr(p.polqual, p.polrelid), ''),
                   coalesce(pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid), '')
              from pg_catalog.pg_policy p
              join pg_catalog.pg_class c on c.oid = p.polrelid
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var policies = new List<PolicyRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            policies.Add(new PolicyRow(
                await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<string>(1, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<string>(2, TestContext.Current.CancellationToken)));
        }

        return policies;
    }

    /// <summary>
    /// True when one of the policy's expressions carries the predicate, judged by
    /// <see cref="IsolationSelfCheck.MatchesPredicate"/> — the production matcher, shared
    /// deliberately. What is independent here is the evidence, not the rule: this test reads
    /// the catalogue with its own SQL and states the two predicates itself, while the matcher
    /// is one implementation, because two copies of a structural match are exactly how
    /// `(pred AND x) OR true` passed the boot gate and this lint at the same time (W7-SEC-01).
    /// </summary>
    private static bool Matches(PolicyRow policy, string predicate) =>
        IsolationSelfCheck.MatchesPredicate(policy.Using, predicate)
        || IsolationSelfCheck.MatchesPredicate(policy.WithCheck, predicate);

    private sealed record TableRow(string Name, bool RowLevelSecurityEnabled, bool RowLevelSecurityForced, bool HasOrgId);

    private sealed record PolicyRow(string Table, string Using, string WithCheck);
}
