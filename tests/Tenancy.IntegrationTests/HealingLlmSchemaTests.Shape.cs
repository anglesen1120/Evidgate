using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class HealingLlmSchemaTests
{
    // ------------------------------------------------------------------ (a) shape, keys, RLS flags

    [Fact]
    public async Task Tables_DeclareTheContractShape()
    {
        var database = await _fixture.SharedDatabaseAsync();

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

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{CallTable} where org_id = '{world.OrgA}'"))
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
}
