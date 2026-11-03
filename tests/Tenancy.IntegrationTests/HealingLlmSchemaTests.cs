using Npgsql;
using Shouldly;
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
/// every app grant and inert here, since no app statement updates a call row (asserted below).
/// </description></item>
/// </list>
/// <para>
/// The cap writer is OWED, not granted: a tenant transaction able to write <c>period_cap_cost</c> or
/// <c>period_cap_tokens</c> could raise its own ceiling, so every test that needs a budget row seeds it
/// through the owner connection and the app-role cases below prove the refusal (42501).
/// </para>
/// <para>
/// The retry/timeout/budget behaviour itself is 08.5.1-T1.3/T1.4's (the gateway records the attempt
/// rows); what this file pins is the shape those rows must satisfy. <c>0055</c> re-declares both tenant
/// policies <c>to evidgate_app</c> and revokes the operator's SELECT: a <c>TO</c>-less policy applies to
/// PUBLIC, which let <c>evidgate_operator</c>'s grant reach every tenant. The local projection below
/// resolves the role list by name, so the expected row pins that clause too.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class HealingLlmSchemaTests
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
        var database = await _fixture.CreateDatabaseAsync();
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
            (world.OrgA, world.ProjectA, "llm-a"),
            (world.OrgB, world.ProjectB, "llm-b"),
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

    // ------------------------------------------------------------------ (a) shape, keys, RLS flags

    [Fact]
    public async Task Tables_DeclareTheContractShape()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.TableExistsAsync(database, CallTable)).ShouldBeTrue();
        (await _fixture.TableExistsAsync(database, BudgetTable)).ShouldBeTrue();

        // The exact column list, in order — which is AC6 as well: there is no secret, key, prompt text,
        // model response or payload column to fill, and an added one fails here before it can be written.
        // `updated_at`/`version` are the 00 §7 convention columns (see the trigger assertion below and the
        // grant case): required on every table, outside every app-role grant, inert on an append-only row.
        ColumnNames(await _fixture.ColumnsAsync(database, CallTable)).ShouldBe(
            "id,org_id,project_id,incident_id,candidate_id,purpose,model,prompt_version,tokens,cost,status,attempt,"
            + "redaction_report_hash,created_at,updated_at,version",
            "AC6: the call ledger's whole content is attribution, counts, a redaction hash and the 00 §7 audit columns");

        ColumnNames(await _fixture.ColumnsAsync(database, BudgetTable)).ShouldBe(
            "id,org_id,project_id,period,period_start,period_cap_cost,period_cap_tokens,used_cost,used_tokens,"
            + "created_at,updated_at,version",
            "the budget counter carries the 00 §7 audit columns the consume trigger owns");

        var callColumns = await _fixture.ColumnsAsync(database, CallTable);
        foreach (var prefix in new[]
        {
            "org_id:uuid:uuid:NO:",
            "project_id:uuid:uuid:NO:",
            "incident_id:uuid:uuid:YES:",
            "candidate_id:uuid:uuid:YES:",
            "tokens:integer:int4:NO:",
            "cost:numeric:numeric:NO:",
            "status:text:text:NO:",
            "attempt:integer:int4:NO:",
            "redaction_report_hash:text:text:YES:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            callColumns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"{CallTable} needs the 0054 contract column shape '{prefix}'");
        }

        // An attempt row is history, and the two 00 §7 columns say so by staying at their defaults: they
        // exist because §7 (M1W3-01) requires the shape — columns plus the shared trigger — on every
        // table, and no app statement can name them (the grant case) or update the row at all.
        (await _fixture.ColumnAsync<string>(
                database,
                "select t.tgname from pg_catalog.pg_trigger t "
                + "join pg_catalog.pg_class c on c.oid = t.tgrelid "
                + "where not t.tgisinternal and c.relname = 'healing_llm_call' order by 1"))
            .ShouldBe(
                ["healing_llm_call_set_updated_at_and_version"],
                $"{CallTable} carries exactly the shared version trigger, as 00 §7 (M1W3-01) requires");

        var budgetColumns = await _fixture.ColumnsAsync(database, BudgetTable);
        foreach (var prefix in new[]
        {
            "period:text:text:NO:",
            "period_start:date:date:NO:",
            "period_cap_cost:numeric:numeric:NO:",
            "period_cap_tokens:integer:int4:NO:",
            "used_cost:numeric:numeric:NO:",
            "used_tokens:integer:int4:NO:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            budgetColumns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"{BudgetTable} needs the 0054 contract column shape '{prefix}'");
        }

        var callConstraints = await _fixture.ConstraintsAsync(database, CallTable);

        callConstraints.ShouldContain(
            constraint => constraint.StartsWith("u:healing_llm_call_org_id_key:", StringComparison.Ordinal)
                && constraint.Contains("UNIQUE (org_id, id)", StringComparison.Ordinal),
            $"{CallTable} needs unique (org_id, id) as the composite tenant key (TL T3)");

        callConstraints.ShouldContain(
            constraint => constraint.StartsWith("f:healing_llm_call_project_fk:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, project_id)", StringComparison.Ordinal)
                && constraint.Contains("project(org_id, id)", StringComparison.Ordinal)
                && constraint.Contains("ON DELETE CASCADE", StringComparison.Ordinal),
            "the project reference must be the composite (org_id, project_id) -> project (org_id, id) key");

        foreach (var check in new[]
        {
            "c:healing_llm_call_purpose_check:",
            "c:healing_llm_call_status_check:",
            "c:healing_llm_call_tokens_check:",
            "c:healing_llm_call_cost_check:",
            "c:healing_llm_call_attempt_check:",
            "c:healing_llm_call_authoring_attribution_check:",
            "c:healing_llm_call_incident_required_check:",
            "c:healing_llm_call_candidate_incident_check:",
            "c:healing_llm_call_version_check:",
        })
        {
            callConstraints.ShouldContain(
                constraint => constraint.StartsWith(check, StringComparison.Ordinal),
                $"{CallTable} needs the CHECK '{check}'");
        }

        var budgetConstraints = await _fixture.ConstraintsAsync(database, BudgetTable);

        budgetConstraints.ShouldContain(
            constraint => constraint.StartsWith("u:healing_llm_budget_org_id_key:", StringComparison.Ordinal)
                && constraint.Contains("UNIQUE (org_id, id)", StringComparison.Ordinal),
            $"{BudgetTable} needs unique (org_id, id) as the composite tenant key (TL T3)");

        // AC3's atomic-consume target: one counter row per project, period and period start.
        budgetConstraints.ShouldContain(
            constraint => constraint.StartsWith("u:healing_llm_budget_period_key:", StringComparison.Ordinal)
                && constraint.Contains("UNIQUE (org_id, project_id, period, period_start)", StringComparison.Ordinal),
            $"{BudgetTable} needs unique (org_id, project_id, period, period_start) as the consume target");

        budgetConstraints.ShouldContain(
            constraint => constraint.StartsWith("f:healing_llm_budget_project_fk:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, project_id)", StringComparison.Ordinal)
                && constraint.Contains("project(org_id, id)", StringComparison.Ordinal)
                && constraint.Contains("ON DELETE CASCADE", StringComparison.Ordinal),
            "the budget's project reference must be the composite key too");

        foreach (var check in new[]
        {
            "c:healing_llm_budget_period_check:",
            "c:healing_llm_budget_period_cap_cost_check:",
            "c:healing_llm_budget_period_cap_tokens_check:",
            "c:healing_llm_budget_used_cost_check:",
            "c:healing_llm_budget_used_tokens_check:",
            "c:healing_llm_budget_version_check:",
        })
        {
            budgetConstraints.ShouldContain(
                constraint => constraint.StartsWith(check, StringComparison.Ordinal),
                $"{BudgetTable} needs the CHECK '{check}'");
        }

        // 0054 defers incident_id/candidate_id: their tables do not exist yet, so the only foreign key
        // is the project one (the deferral is recorded in the migration's header).
        callConstraints.ShouldNotContain(
            constraint => constraint.StartsWith("f:", StringComparison.Ordinal)
                && (constraint.Contains("incident", StringComparison.Ordinal)
                    || constraint.Contains("candidate", StringComparison.Ordinal)),
            "0054 defers the incident/candidate FKs to the migration that creates those tables");

        var callIndexes = await _fixture.IndexesAsync(database, CallTable);
        callIndexes.ShouldContain(
            index => index.StartsWith("healing_llm_call_project_created_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id, created_at)", StringComparison.Ordinal),
            "the budget-window sum is served by healing_llm_call_project_created_idx");
        callIndexes.ShouldContain(
            index => index.StartsWith("healing_llm_call_project_purpose_created_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id, purpose, created_at)", StringComparison.Ordinal),
            "the per-purpose cost report is served by healing_llm_call_project_purpose_created_idx");

        var budgetIndexes = await _fixture.IndexesAsync(database, BudgetTable);
        budgetIndexes.ShouldContain(
            index => index.StartsWith("healing_llm_budget_period_key:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id, period, period_start)", StringComparison.Ordinal),
            "the consume statement's equality tuple is the unique key's index");

        foreach (var table in new[] { CallTable, BudgetTable })
        {
            var rls = await _fixture.RowSecurityAsync(database, table);
            rls.Enabled.ShouldBeTrue($"DECISIONS F5: {table} is under ENABLE row level security");
            rls.Forced.ShouldBeTrue($"DECISIONS F5: FORCE is what makes the owner obey its own policies too");
        }

        // Two policies each, and nothing else: the app-role tenant predicate plus the owner-only backfill
        // policy FORCE makes necessary. Both tables are org-scoped (org_id NOT NULL), so neither appears in
        // contracts/rls-exceptions.txt.
        (await PolicyRowsAsync(database, CallTable)).ShouldBe(
            [
                "healing_llm_call_owner_backfill:evidgate_owner:*:true:true",
                "tenant_isolation:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            ],
            Case.Sensitive,
            $"{CallTable} declares exactly the tenant policy and the owner backfill policy");

        (await PolicyRowsAsync(database, BudgetTable)).ShouldBe(
            [
                "healing_llm_budget_owner_backfill:evidgate_owner:*:true:true",
                "tenant_isolation:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            ],
            Case.Sensitive,
            $"{BudgetTable} declares exactly the tenant policy and the owner backfill policy");
    }

    // ------------------------------------------------------------------ (b) closed vocabularies

    [Fact]
    public async Task Purpose_IsAClosedVocabulary()
    {
        var world = await SeedTenantsAsync();

        foreach (var purpose in new[] { "authoring", "triage", "AUTHORING_GENERATION", "" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, purpose, Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, purpose);
            error.ConstraintName.ShouldBe("healing_llm_call_purpose_check", purpose);
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{CallTable}"))
            .ShouldBe(0, "a refused attempt writes nothing");
    }

    [Fact]
    public async Task Status_IsAClosedVocabulary()
    {
        var world = await SeedTenantsAsync();

        // `schema_violation` is the plan §4 shorthand; the seam and the database spell the retry outcome
        // `llm_schema_violation` (AC1), so the shorthand is not storable.
        foreach (var status in new[] { "schema_violation", "failed", "OK", "" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), status: status));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, status);
            error.ConstraintName.ShouldBe("healing_llm_call_status_check", status);
        }

        // Every storable value is accepted, including the two retry/abort outcomes the gateway records.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            foreach (var status in new[] { "ok", "timeout", "llm_schema_violation", "budget_exceeded", "error" })
            {
                (await scope.ExecuteAsync(
                        InsertCallSql,
                        CallParameters(
                            Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                            Guid.CreateVersion7(), status: status)))
                    .ShouldBe(1, status);
            }

            await scope.CommitAsync();
        }
    }

    // ------------------------------------------------------------------ (c) attribution CHECKs (AC4, AC7)

    [Fact]
    public async Task AuthoringCall_CarriesNoIncidentAndNoCandidate()
    {
        var world = await SeedTenantsAsync();

        // AC7: an authoring generation serves a project — an incident or a candidate on the row is a
        // misattribution, refused whoever writes it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "authoring_generation",
                    Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_authoring_attribution_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "authoring_generation",
                    Guid.CreateVersion7(), Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_authoring_attribution_check");
        }

        // The authoring row AC7 describes — project set, incident and candidate null — is storable, and it
        // is what 13.3.1-T2's `authoring_version.ai_run_id` will name.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var id = Guid.CreateVersion7();
            (await scope.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(id, world.OrgA, world.ProjectA, "authoring_generation")))
                .ShouldBe(1);
            await scope.CommitAsync();

            (await _fixture.ScalarAsync<long>(
                    world.Database,
                    $"select count(*) from public.{CallTable} where id = '{id}' and incident_id is null and candidate_id is null"))
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task NonAuthoringCall_RequiresItsIncident()
    {
        var world = await SeedTenantsAsync();

        foreach (var purpose in new[] { "triage_hypothesis", "candidate_generation" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, purpose));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, purpose);
            error.ConstraintName.ShouldBe("healing_llm_call_incident_required_check", purpose);
        }
    }

    [Fact]
    public async Task Candidate_WithoutAnIncident_IsRefused()
    {
        var world = await SeedTenantsAsync();

        // A candidate refines an incident-scoped call. With no incident the row is already refused by
        // `incident_required_check`; the candidate rule is the redundant backstop a future purpose would
        // otherwise have to remember, so either name is the correct refusal here.
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var error = await scope.CaptureErrorAsync(
            InsertCallSql,
            CallParameters(
                Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                candidate: Guid.CreateVersion7()));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBeOneOf(
            "healing_llm_call_incident_required_check",
            "healing_llm_call_candidate_incident_check");
    }

    // ------------------------------------------------------------------ (d) counts, period, keys

    [Fact]
    public async Task NonNegativeCounts_AndAPositiveAttempt_AreRefused()
    {
        var world = await SeedTenantsAsync();

        // The per-attempt counters the budget and the cost report sum cannot be negative, and attempt is
        // the ordinal inside one invocation, so it starts at 1.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), tokens: -1));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_tokens_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), cost: -0.000001m));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_cost_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), attempt: 0));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_attempt_check");
        }

        // The budget's caps and usage, and its concurrency token.
        var budgetCases = new (string Name, (string Name, object Value)[] Parameters, string Constraint)[]
        {
            (
                "period_cap_cost",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, capCost: -1m),
                "healing_llm_budget_period_cap_cost_check"),
            (
                "period_cap_tokens",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, capTokens: -1),
                "healing_llm_budget_period_cap_tokens_check"),
            (
                "used_cost",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, usedCost: -1m),
                "healing_llm_budget_used_cost_check"),
            (
                "used_tokens",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, usedTokens: -1),
                "healing_llm_budget_used_tokens_check"),
            (
                "version",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, version: 0),
                "healing_llm_budget_version_check"),
        };

        foreach (var (name, parameters, constraint) in budgetCases)
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(InsertOwnerBudgetSql, parameters);

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, name);
            error.ConstraintName.ShouldBe(constraint, name);
        }
    }

    [Fact]
    public async Task BudgetPeriod_IsDailyOnly()
    {
        var world = await SeedTenantsAsync();

        foreach (var period in new[] { "weekly", "monthly", "DAILY", "" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                InsertOwnerBudgetSql,
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, period: period));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, period);
            error.ConstraintName.ShouldBe("healing_llm_budget_period_check", period);
        }
    }

    [Fact]
    public async Task CompositeFk_RefusesAProjectOfAnotherTenant()
    {
        var world = await SeedTenantsAsync();

        // Filed under org B (so the WITH CHECK passes) while naming org A's project: only the composite
        // foreign key can refuse it, which is exactly why the key carries org_id.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgB, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_project_fk");
        }

        // The budget row is provisioned by the owner path (the app role holds no cap privilege, so it
        // cannot raise the cross-tenant question at all); the composite key refuses the same reference
        // for the owner, which is the enforcement this test is about.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCapacitySql,
                CapacityParameters(Guid.CreateVersion7(), world.OrgB, world.ProjectA));

            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldBe("healing_llm_budget_project_fk");
        }
    }

    [Fact]
    public async Task BudgetUniqueKey_RefusesASecondCounterRowForThePeriod()
    {
        var world = await SeedTenantsAsync();

        // Both rows are provisioned through the owner path: the caps are the owner's to set, and the key
        // is about the tuple, not about which role writes it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await scope.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA)))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        // AC3's consume statement matches exactly one row by (org, project, period, period start); a
        // second row for the same tuple would make "did the cap hold" ambiguous, so the key refuses it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCapacitySql,
                CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA));

            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("healing_llm_budget_period_key");
        }
    }

    // ------------------------------------------------------------------ (e) the grants

    [Fact]
    public async Task AppRole_CanInsertAndReadCalls_ButNotRewriteOrRemoveThem()
    {
        var world = await SeedTenantsAsync();

        // Column-level INSERT and nothing table-level (0052 precedent): a table-level grant would let a
        // caller name `created_at` and backdate an attempt.
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "insert"))
            .ShouldBeFalse("INSERT is granted per column, never on the table");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "update"))
            .ShouldBeFalse("0054 grants no UPDATE: the ledger is append-only");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "delete"))
            .ShouldBeFalse("0054 grants no DELETE: the ledger is append-only");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", CallTable, "select"))
            .ShouldBeFalse("0055 revokes the operator's SELECT: the tenant policy names evidgate_app and no read path uses the operator");

        foreach (var column in new[]
        {
            "id", "org_id", "project_id", "incident_id", "candidate_id", "purpose", "model",
            "prompt_version", "tokens", "cost", "status", "attempt", "redaction_report_hash",
        })
        {
            (await _fixture.ScalarAsync<bool>(
                    world.Database,
                    $"select has_column_privilege('evidgate_app', 'public.{CallTable}', '{column}', 'INSERT')"))
                .ShouldBeTrue($"the app role inserts `{column}`");
        }

        (await _fixture.ScalarAsync<bool>(
                world.Database,
                $"select has_column_privilege('evidgate_app', 'public.{CallTable}', 'created_at', 'INSERT')"))
            .ShouldBeFalse("`created_at` belongs to the column default, not to the caller");

        // The 00 §7 convention columns exist for the shape and are outside every app grant: no INSERT, so
        // a caller cannot set the token, and no UPDATE (the table-level refusal above is the whole story,
        // this pins the column-level one too).
        foreach (var column in new[] { "updated_at", "version" })
        {
            (await _fixture.ScalarAsync<bool>(
                    world.Database,
                    $"select has_column_privilege('evidgate_app', 'public.{CallTable}', '{column}', 'INSERT')"))
                .ShouldBeFalse($"`{column}` is outside the app-role INSERT grant (00 §7 convention column)");
            (await _fixture.ScalarAsync<bool>(
                    world.Database,
                    $"select has_column_privilege('evidgate_app', 'public.{CallTable}', '{column}', 'UPDATE')"))
                .ShouldBeFalse($"`{column}` is outside every app-role grant: the ledger is append-only");
        }

        // The recorder's write: one attempt row inside the caller's tenant transaction (T1.2 seam).
        await using (var recorder = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await recorder.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(
                        Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                        Guid.CreateVersion7(), attempt: 2)))
                .ShouldBe(1, "select + a column-level insert is the recorder's whole grant");
            await recorder.CommitAsync();
        }

        // One scope per refused statement: an ACL error aborts its transaction, so the second refusal
        // needs its own transaction to be the ACL's answer and not the aborted state's.
        await using (var rewriting = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await rewriting.CaptureErrorAsync(
                    $"update public.{CallTable} set status = 'error'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the app role cannot rewrite an attempt");
        }

        await using (var removing = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await removing.CaptureErrorAsync($"delete from public.{CallTable}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the app role cannot remove an attempt");
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{CallTable}"))
            .ShouldBe(1, "the refused statements left the ledger alone");

        // The convention token is inert on this table: the recorder's insert left it at the default and
        // nothing the app role can issue moves it (there is no UPDATE grant at all).
        (await _fixture.ScalarAsync<int>(world.Database, $"select version from public.{CallTable}"))
            .ShouldBe(1, "the 00 §7 token stays at the column default 0054 gives it");
    }

    [Fact]
    public async Task AppRole_ConsumesTheBudgetRow_ButCannotReTargetIt()
    {
        var world = await SeedTenantsAsync();

        // Provisioning is the owner path's: the caps are NOT in the app role's INSERT grant, and a row
        // that could name them would let a tenant raise its own ceiling (owed to 08.5.1-T1.4/T1.7).
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await seed.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA)))
                .ShouldBe(1, "the owner provisions the cap row the consume runs against");
            await seed.CommitAsync();
        }

        // AC3's consume: the update the hard stop runs, admitted on exactly the columns it moves.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync(
                    $"""
                    update public.{BudgetTable}
                       set used_cost = used_cost + 0.000250, used_tokens = used_tokens + 12
                     where org_id = @org_id
                       and project_id = @project_id
                       and period = 'daily'
                       and used_cost + 0.000250 <= period_cap_cost
                    """,
                    ("org_id", world.OrgA),
                    ("project_id", world.ProjectA)))
                .ShouldBe(1, "the consumption columns are the app role's whole UPDATE grant");
            await scope.CommitAsync();
        }

        // `version` moved to 2 with no column privilege on it: the 0004 trigger assigns `updated_at` and
        // `version` to NEW, and PostgreSQL checks the columns a statement names, not a trigger's writes.
        (await _fixture.ScalarAsync<string>(
                world.Database,
                $"select used_cost::text || ':' || used_tokens || ':' || version from public.{BudgetTable}"))
            .ShouldBe("0.000250:12:2", "the trigger moved version on the consume, as 0004 does");

        // Everything else is outside the grant: the caps (raising one is exactly the ceiling escape this
        // test refuses), the trigger-owned token, the identity, the period and the creation stamp. One
        // scope per statement, because an ACL error aborts the transaction that caused it.
        foreach (var set in new[]
        {
            "period_cap_cost = period_cap_cost + 1",
            "period_cap_tokens = period_cap_tokens + 1",
            "updated_at = now()",
            "version = 1",
            "id = gen_random_uuid()",
            $"org_id = '{Guid.CreateVersion7()}'",
            "period = 'weekly'",
            "period_start = current_date + 1",
            "created_at = now()",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA);
            (await scope.CaptureErrorAsync($"update public.{BudgetTable} set {set}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, set);
        }

        await using (var removing = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await removing.CaptureErrorAsync($"delete from public.{BudgetTable}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the app role cannot remove a counter row");
        }
    }

    [Fact]
    public async Task AppRole_CannotProvisionOrRaiseTheBudgetRow()
    {
        var world = await SeedTenantsAsync();

        // The grant matrix, column by column: the app role may move the two usage counters and name the
        // five identity/period columns on INSERT, and nothing else.
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "select"))
            .ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "insert"))
            .ShouldBeFalse("INSERT is granted per column, never on the table");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "update"))
            .ShouldBeFalse("UPDATE is granted per column, never on the table");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "delete"))
            .ShouldBeFalse("0054 grants no DELETE: the project cascade is the only delete path");

        foreach (var column in new[] { "id", "org_id", "project_id", "period", "period_start" })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "INSERT"))
                .ShouldBeTrue($"the app role names `{column}` on INSERT");
        }

        foreach (var column in new[]
        {
            "period_cap_cost", "period_cap_tokens", "used_cost", "used_tokens", "created_at",
            "updated_at", "version",
        })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "INSERT"))
                .ShouldBeFalse($"`{column}` is not the caller's to name on INSERT");
        }

        foreach (var column in new[] { "used_cost", "used_tokens" })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "UPDATE"))
                .ShouldBeTrue($"the consume moves `{column}`");
        }

        foreach (var column in new[]
        {
            "period_cap_cost", "period_cap_tokens", "updated_at", "version",
            "id", "org_id", "project_id", "period", "period_start", "created_at",
        })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "UPDATE"))
                .ShouldBeFalse($"`{column}` is outside the app role's UPDATE grant");
        }

        // The cap INSERT the tenants' own path would need is refused at the ACL, so no tenant transaction
        // can even reach the NOT NULL check with a cap it chose.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCapacitySql,
                CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA));

            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the caps are not the app role's to name");
        }

        // And the residual INSERT grant cannot complete a row either: with the caps outside it, the NOT
        // NULL cap column refuses a statement that names only what the app role may name.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                """
                insert into public.healing_llm_budget
                  (id, org_id, project_id, period, period_start)
                values (@id, @org_id, @project_id, @period, @period_start)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("project_id", world.ProjectA),
                ("period", "daily"),
                ("period_start", PeriodStart));

            error.SqlState.ShouldBe(PostgresErrorCodes.NotNullViolation, "no app-role statement can provision a budget row");
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{BudgetTable}"))
            .ShouldBe(0, "the refused statements left no counter row behind");
    }

    [Fact]
    public async Task Tenants_SeeTheirOwnCallsAndBudget_AndNothingElse()
    {
        var world = await SeedTenantsAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(
                        Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                        Guid.CreateVersion7()))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            (await scope.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(
                        Guid.CreateVersion7(), world.OrgB, world.ProjectB, "candidate_generation",
                        Guid.CreateVersion7()))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // One cap row per project, provisioned through the owner path: the app role cannot name a cap, so
        // the tenant's own scope only ever reads the row and moves its usage.
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await seed.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA)))
                .ShouldBe(1);
            (await seed.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgB, world.ProjectB)))
                .ShouldBe(1);
            await seed.CommitAsync();
        }

        // Org A sees one call and one counter, both its own; org B's rows are invisible — and an UPDATE
        // aimed at them matches nothing rather than reaching across the boundary.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ScalarAsync<long>($"select count(*) from public.{CallTable}")).ShouldBe(1);
            (await scope.ScalarAsync<long>(
                    $"select count(*) from public.{CallTable} where org_id = @org_id",
                    ("org_id", world.OrgA))).ShouldBe(1, "the visible row is this org's own");
            (await scope.ScalarAsync<long>($"select count(*) from public.{BudgetTable}")).ShouldBe(1);
            (await scope.ExecuteAsync(
                    $"update public.{BudgetTable} set used_tokens = used_tokens + 1"))
                .ShouldBe(1, "only this org's counter row is reachable by the consume");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            (await scope.ScalarAsync<long>(
                    $"select count(*) from public.{CallTable} where org_id = @org_id",
                    ("org_id", world.OrgB))).ShouldBe(1);
            (await scope.ScalarAsync<long>(
                    $"select count(*) from public.{BudgetTable} where org_id = @org_id",
                    ("org_id", world.OrgB))).ShouldBe(1);
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{CallTable}"))
            .ShouldBe(2, "both tenants' rows are there; only the policy hides them");
    }

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
