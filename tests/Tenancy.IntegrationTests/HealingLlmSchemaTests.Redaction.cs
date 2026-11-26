using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Migration 0136 as the database sees it: the redaction report is tenant-scoped evidence nobody rewrites, and a
/// <c>blocked_leak</c> attempt row cannot exist without the hash of the report that blocked it.
/// </summary>
public sealed partial class HealingLlmSchemaTests
{
    private const string InsertReportSql =
        """
        insert into public.healing_redaction_report
          (id, org_id, project_id, incident_id, candidate_id, purpose, rule_version, report, report_hash)
        values (@id, @org_id, @project_id, null, null, 'authoring_generation', 'evg-redact-v1', @report::jsonb, @hash)
        """;

    private const string InsertCallWithHashSql =
        """
        insert into public.healing_llm_call
          (id, org_id, project_id, purpose, model, prompt_version, tokens, cost, status, attempt, redaction_report_hash)
        values (@id, @org_id, @project_id, 'authoring_generation', 'm', 'v1', 0, 0, @status, 1, @hash)
        """;

    private static readonly string GoodHash = "sha256:" + new string('b', 64);

    private static (string Name, object Value)[] ReportParameters(Guid id, Guid org, Guid project, string hash) =>
    [
        ("id", id), ("org_id", org), ("project_id", project), ("report", "{\"findings\":[]}"), ("hash", hash),
    ];

    [Fact]
    public async Task RedactionReport_IsTenantScopedAndAppendOnly()
    {
        var world = await SeedTenantsAsync();
        var id = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync(InsertReportSql, ReportParameters(id, world.OrgA, world.ProjectA, GoodHash))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.healing_redaction_report")).ShouldBe(0L);
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var update = await scope.CaptureErrorAsync(
                "update public.healing_redaction_report set rule_version = 'x' where id = @id", ("id", id));
            update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var update = await scope.CaptureErrorAsync(
                "update public.healing_redaction_report set rule_version = 'x' where id = @id", ("id", id));
            update.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            update.MessageText.ShouldContain("healing_redaction_report_immutable");
        }
    }

    [Fact]
    public async Task RedactionReport_RefusesAMalformedHashAndAProjectOfAnotherTenant()
    {
        var world = await SeedTenantsAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                InsertReportSql, ReportParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, "sha256:short"));
            error.ConstraintName.ShouldBe("healing_redaction_report_hash_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            var error = await scope.CaptureErrorAsync(
                InsertReportSql, ReportParameters(Guid.CreateVersion7(), world.OrgB, world.ProjectA, GoodHash));
            error.ConstraintName.ShouldBe("healing_redaction_report_project_fk");
        }
    }

    [Fact]
    public async Task BlockedLeakCall_NeedsAWellFormedReportHash()
    {
        var world = await SeedTenantsAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var withoutHash = await scope.CaptureErrorAsync(
                "insert into public.healing_llm_call (id, org_id, project_id, purpose, model, prompt_version, tokens, cost, status, attempt)"
                + " values (@id, @org, @project, 'authoring_generation', 'm', 'v1', 0, 0, 'blocked_leak', 1)",
                ("id", Guid.CreateVersion7()), ("org", world.OrgA), ("project", world.ProjectA));
            withoutHash.ConstraintName.ShouldBe("healing_llm_call_blocked_report_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var malformed = await scope.CaptureErrorAsync(
                InsertCallWithHashSql,
                ("id", Guid.CreateVersion7()), ("org_id", world.OrgA), ("project_id", world.ProjectA),
                ("status", "blocked_leak"), ("hash", "not-a-hash"));
            malformed.ConstraintName.ShouldBe("healing_llm_call_report_hash_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await scope.ExecuteAsync(
                    InsertCallWithHashSql,
                    ("id", Guid.CreateVersion7()), ("org_id", world.OrgA), ("project_id", world.ProjectA),
                    ("status", "blocked_leak"), ("hash", GoodHash)))
                .ShouldBe(1);
            await scope.CommitAsync();
        }
    }
}
