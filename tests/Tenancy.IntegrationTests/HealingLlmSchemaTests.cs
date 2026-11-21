using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 08.5.1-T1.1 SQL level: migration 0054's <c>healing_llm_call</c> (the append-only ledger of every
/// provider attempt) and <c>healing_llm_budget</c> (the rolling daily cap a project consumes), as the
/// database sees them.
/// <list type="bullet">
/// <item><description>
/// the shapes — the exact column lists (which is also AC6: no secret, prompt or response column exists
/// to be filled), the <c>unique (org_id, id)</c> composite-FK targets, the composite project keys, the
/// closed <c>purpose</c>/<c>status</c>/<c>period</c> vocabularies, the attribution CHECKs (AC4/AC7) and
/// the non-negative counts, the two call indexes and the budget's atomic-consume unique key (AC3);
/// </description></item>
/// <item><description>
/// the tenant boundary: ENABLE + FORCE row level security on both tables, the evidgate_app-only
/// <c>tenant_isolation</c> policy plus the owner-only backfill policy, and a foreign org's call and
/// budget rows invisible to (and unwritable by) another tenant;
/// </description></item>
/// <item><description>
/// the grants: the call ledger is select + a column-level insert and nothing else (append-only); the
/// budget row is select + an UPDATE of exactly <c>used_cost</c>/<c>used_tokens</c> — the caps (the
/// owner/provisioning path's to set, owed to 08.5.1-T1.4/T1.7) and the trigger-owned
/// <c>updated_at</c>/<c>version</c> are outside every app-role grant — so neither table can be
/// rewritten, re-targeted or removed by the application role, and no app statement can provision a
/// budget row or raise a cap. The ledger carries the 00 §7 <c>updated_at</c>/<c>version</c> columns and
/// the shared version trigger because the convention requires them on every table; they are outside
/// every app grant and inert here, since no app statement updates a call row (asserted in the Grants part).
/// </description></item>
/// </list>
/// <para>
/// The cap writer is OWED, not granted: a tenant transaction able to write <c>period_cap_cost</c> or
/// <c>period_cap_tokens</c> could raise its own ceiling, so every test that needs a budget row seeds it
/// through the owner connection and the app-role cases in the Grants part prove the refusal (42501).
/// </para>
/// <para>
/// The retry/timeout/budget behaviour itself is 08.5.1-T1.3/T1.4's (the gateway records the attempt
/// rows); what this class pins is the shape those rows must satisfy. <c>0055</c> re-declares both tenant
/// policies <c>to evidgate_app</c> and revokes the operator's SELECT: a <c>TO</c>-less policy applies to
/// PUBLIC, which let <c>evidgate_operator</c>'s grant reach every tenant. The local projection below
/// resolves the role list by name, so the expected row pins that clause too.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class HealingLlmSchemaTests
{
    private const string CallTable = "healing_llm_call";

    private const string BudgetTable = "healing_llm_budget";

    /// <summary>One attempt row, as 08.5.1-T1.2's recorder inserts it (T30 attribution columns included).</summary>
    private const string InsertCallSql =
        """
        insert into public.healing_llm_call
          (id, org_id, project_id, incident_id, candidate_id, purpose, model, prompt_version,
           tokens, cost, status, attempt, redaction_report_hash)
        values (@id, @org_id, @project_id, @incident_id, @candidate_id, @purpose, 'mock-deterministic',
                'prompt-v1', @tokens, @cost, @status, @attempt, null)
        """;

    /// <summary>
    /// A budget INSERT naming the caps. The app role holds no privilege on either cap column, so this
    /// statement is refused (42501) for it; the rows these tests need are provisioned through the owner
    /// connection, which is the path 08.5.1-T1.4/T1.7 owe.
    /// </summary>
    private const string InsertCapacitySql =
        """
        insert into public.healing_llm_budget
          (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens)
        values (@id, @org_id, @project_id, @period, @period_start, @period_cap_cost, @period_cap_tokens)
        """;

    /// <summary>The owner's budget INSERT, naming the columns a caller may not, so a CHECK can be isolated.</summary>
    private const string InsertOwnerBudgetSql =
        """
        insert into public.healing_llm_budget
          (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens,
           used_cost, used_tokens, version)
        values (@id, @org_id, @project_id, @period, @period_start, @period_cap_cost, @period_cap_tokens,
                @used_cost, @used_tokens, @version)
        """;

    private static readonly DateOnly PeriodStart = new(2026, 10, 29);

    private readonly PostgresTenancyFixture _fixture;

    public HealingLlmSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>Two migrated tenants, each with one project, and the ids that address them.</summary>
    private sealed record World(string Database, Guid OrgA, Guid OrgB, Guid ProjectA, Guid ProjectB);

    private async Task<World> SeedTenantsAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var world = new World(
            database,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());

        // The organization row's own id IS the tenant (TL T4), so the seed writes the root row with the
        // org id the rest of the test uses — the same write-through-owner path the other schema suites take.
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        foreach (var (org, project, slug) in new[]
        {
            (world.OrgA, world.ProjectA, Unique.Slug("llm-a")),
            (world.OrgB, world.ProjectB, Unique.Slug("llm-b")),
        })
        {
            await owner.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", org),
                ("name", slug),
                ("slug", slug));
            await owner.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", project),
                ("org_id", org),
                ("name", slug),
                ("slug", slug));
        }

        await owner.CommitAsync();
        return world;
    }

    // ------------------------------------------------------------------ one parameter set per row shape

    private static (string Name, object Value)[] CallParameters(
        Guid id,
        Guid org,
        Guid project,
        string purpose,
        Guid? incident = null,
        Guid? candidate = null,
        int tokens = 12,
        decimal cost = 0.000250m,
        string status = "ok",
        int attempt = 1) =>
    [
        ("id", id),
        ("org_id", org),
        ("project_id", project),
        ("incident_id", (object?)incident ?? DBNull.Value),
        ("candidate_id", (object?)candidate ?? DBNull.Value),
        ("purpose", purpose),
        ("tokens", tokens),
        ("cost", cost),
        ("status", status),
        ("attempt", attempt),
    ];

    private static (string Name, object Value)[] CapacityParameters(
        Guid id,
        Guid org,
        Guid project,
        string period = "daily",
        decimal capCost = 1.000000m,
        int capTokens = 1000) =>
    [
        ("id", id),
        ("org_id", org),
        ("project_id", project),
        ("period", period),
        ("period_start", PeriodStart),
        ("period_cap_cost", capCost),
        ("period_cap_tokens", capTokens),
    ];

    private static (string Name, object Value)[] OwnerBudgetParameters(
        Guid id,
        Guid org,
        Guid project,
        string period = "daily",
        decimal capCost = 1.000000m,
        int capTokens = 1000,
        decimal usedCost = 0m,
        int usedTokens = 0,
        int version = 1) =>
    [
        ("id", id),
        ("org_id", org),
        ("project_id", project),
        ("period", period),
        ("period_start", PeriodStart),
        ("period_cap_cost", capCost),
        ("period_cap_tokens", capTokens),
        ("used_cost", usedCost),
        ("used_tokens", usedTokens),
        ("version", version),
    ];

    /// <summary>
    /// <c>has_column_privilege</c> for <c>evidgate_app</c> on one column of <paramref name="table"/>: the
    /// form the app role's actually-reachable writes take, since both budget grants are per column.
    /// </summary>
    private Task<bool> ColumnPrivilegeAsync(string database, string table, string column, string privilege) =>
        _fixture.ScalarAsync<bool>(
            database,
            $"select has_column_privilege('evidgate_app', 'public.{table}', '{column}', '{privilege}')");

    /// <summary>The table's column names in physical order, from the <see cref="PostgresTenancyFixture.ColumnsAsync"/> projection.</summary>
    private static string ColumnNames(IReadOnlyList<string> columns) =>
        string.Join(",", columns.Select(column => column.Split(':')[0]));

    /// <summary>
    /// Every policy of <paramref name="table"/> as <c>name:role:cmd:using:withcheck</c>, naming the PUBLIC
    /// role as <c>PUBLIC</c> (a <c>TO</c>-less policy stores <c>polroles = {0}</c>, which has no
    /// <c>pg_roles</c> row, so <see cref="PostgresTenancyFixture.PoliciesAsync"/> cannot name it).
    /// </summary>
    private Task<IReadOnlyList<string>> PolicyRowsAsync(string database, string table) =>
        _fixture.ColumnAsync<string>(
            database,
            $"""
            select p.polname || ':' || coalesce((select r.rolname from pg_roles r where r.oid = role_oid), 'PUBLIC')
                   || ':' || p.polcmd::text || ':' ||
                   coalesce(pg_get_expr(p.polqual, p.polrelid), '-') || ':' ||
                   coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '-')
              from pg_policy p
              cross join lateral unnest(p.polroles) as role_oid
             where p.polrelid = 'public.{table}'::regclass
             order by p.polname
            """);
}
