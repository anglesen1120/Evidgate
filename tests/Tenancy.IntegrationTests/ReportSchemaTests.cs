using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The database side of <c>test_report</c>: immutability, grants, row level security, and the checks that bound a snapshot.</summary>
[Trait("Category", "Docker")]
public sealed class ReportSchemaTests
{
    private static readonly string[] InsertableColumns =
    [
        "id", "org_id", "project_id", "job_id", "scope_kind", "selected_ids", "sections", "language", "schema_version",
        "pinned_versions", "payload", "content_hash", "created_by",
    ];

    private static readonly string[] ServerMaintainedColumns = ["created_at", "updated_at", "version"];

    private readonly PostgresTenancyFixture _fixture;

    public ReportSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AppRole_CanInsertButCannotUpdateOrDeleteAReport()
    {
        await using var world = await ReportTestWorld.StartAsync(_fixture);
        var app = _fixture.AppConnectionString(world.Database);
        var id = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            // Positive control: the same scope may write the row the refusals below target.
            (await scope.ExecuteAsync(Insert(world, id))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            (await scope.CaptureErrorAsync($"update public.test_report set language = 'en' where id = '{id:D}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            (await scope.CaptureErrorAsync($"delete from public.test_report where id = '{id:D}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "test_report", "DELETE")).ShouldBeFalse();
        foreach (var column in InsertableColumns)
        {
            (await ColumnPrivilegeAsync(world, column, "INSERT")).ShouldBeTrue($"{column} is written by the job");
        }

        foreach (var column in ServerMaintainedColumns)
        {
            (await ColumnPrivilegeAsync(world, column, "INSERT")).ShouldBeFalse($"{column} is maintained by the server");
        }

        foreach (var column in InsertableColumns.Concat(ServerMaintainedColumns))
        {
            (await ColumnPrivilegeAsync(world, column, "UPDATE")).ShouldBeFalse($"{column} is never updated");
        }
    }

    [Fact]
    public async Task Trigger_RefusesAnUpdateEvenForTheOwner_AndAllowsTheOwnerErasure()
    {
        await using var world = await ReportTestWorld.StartAsync(_fixture);
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, Insert(world, id));

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org))
        {
            (await owner.CaptureErrorAsync($"update public.test_report set payload = '{{\"x\":1}}' where id = '{id:D}'"))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using var erase = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);
        (await erase.ExecuteAsync($"delete from public.test_report where id = '{id:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Rows_AreInvisibleToAnotherTenantAndToAScopeWithoutContext()
    {
        await using var world = await ReportTestWorld.StartAsync(_fixture);
        await using var other = await ReportTestWorld.StartAsync(_fixture);
        await _fixture.ExecuteAsync(world.Database, Insert(world, Guid.CreateVersion7()));
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.test_report")).ShouldBeGreaterThanOrEqualTo(1);
        }

        await using (var scope = await TenantScope.BeginAsync(app, other.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.test_report")).ShouldBe(0);
            (await scope.CaptureErrorAsync(Insert(world, Guid.CreateVersion7()))).SqlState
                .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var bare = await TenantScope.BeginAsync(app);
        (await bare.ScalarAsync<long>("select count(*) from public.test_report")).ShouldBe(0);
    }

    [Fact]
    public async Task Table_ForcesRowLevelSecurity_AndItsAppPolicyIsForTheAppRole()
    {
        await using var world = await ReportTestWorld.StartAsync(_fixture);

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.test_report'::regclass")).ShouldBeTrue();
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select array_to_string(roles, ',') from pg_policies where tablename = 'test_report' and policyname = 'test_report_tenant'"))
            .ShouldBe("evidgate_app");
    }

    [Fact]
    public async Task Checks_BoundWhatASnapshotMayHold_AndOneJobHoldsOneReport()
    {
        await using var world = await ReportTestWorld.StartAsync(_fixture);
        var job = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, Insert(world, Guid.CreateVersion7(), job));

        await Refused(world, Insert(world, Guid.CreateVersion7(), job), PostgresErrorCodes.UniqueViolation);
        await Refused(world, Insert(world, Guid.CreateVersion7(), scope: "everything"), PostgresErrorCodes.CheckViolation);
        await Refused(world, Insert(world, Guid.CreateVersion7(), language: "fr"), PostgresErrorCodes.CheckViolation);
        await Refused(world, Insert(world, Guid.CreateVersion7(), hash: "md5:abc"), PostgresErrorCodes.CheckViolation);
        await Refused(world, Insert(world, Guid.CreateVersion7(), ids: "[]"), PostgresErrorCodes.CheckViolation);
        await Refused(world, Insert(world, Guid.CreateVersion7(), payload: "[]"), PostgresErrorCodes.CheckViolation);
        await Refused(world, Insert(world, Guid.CreateVersion7(), schemaVersion: 0), PostgresErrorCodes.CheckViolation);
        await Refused(
            world,
            Insert(world, Guid.CreateVersion7()).Replace($"'{world.Project:D}'", $"'{Guid.CreateVersion7():D}'", StringComparison.Ordinal),
            PostgresErrorCodes.ForeignKeyViolation);
    }

    private Task<bool> ColumnPrivilegeAsync(RequirementTestWorld world, string column, string privilege) => _fixture.ScalarAsync<bool>(
        world.Database,
        $"select has_column_privilege('evidgate_app', 'public.test_report', '{column}', '{privilege}')");

    private async Task Refused(RequirementTestWorld world, string sql, string sqlState)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(sql)).SqlState.ShouldBe(sqlState);
    }

    private static string Insert(
        RequirementTestWorld world,
        Guid id,
        Guid? job = null,
        string scope = "feature_nodes",
        string language = "vi",
        string? hash = null,
        string ids = "[\"00000000-0000-7000-8000-00000000000a\"]",
        string payload = "{}",
        int schemaVersion = 1) => $"""
        insert into public.test_report
          (id, org_id, project_id, job_id, scope_kind, selected_ids, sections, language, schema_version, pinned_versions, payload, content_hash)
        values
          ('{id:D}', '{world.Org:D}', '{world.Project:D}', '{job ?? Guid.CreateVersion7():D}', '{scope}', '{ids}'::jsonb,
           '["traceability"]'::jsonb, '{language}', {schemaVersion}, jsonb_build_object(), '{payload}'::jsonb, '{hash ?? "sha256:" + new string('a', 64)}')
        """;
}
