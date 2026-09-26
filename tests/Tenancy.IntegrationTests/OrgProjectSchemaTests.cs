using Evidgate.Modules.Tenancy.Contracts;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC1 / AC7 and 02.1.1-T1.1/T1.2: <c>organization</c> (migration 0004) and
/// <c>project</c> (migration 0005) carry the keys, the covering index, ENABLE +
/// FORCE row level security and the app-tenant + owner policies exactly as this
/// task declares them (00 §5.4, §7, DECISIONS F5). The `TO evidgate_operator`
/// policy is absent by design: it is 02.8.1's grant-gated policy (TL T76).
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class OrgProjectSchemaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public OrgProjectSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// T1.1: the <c>organization</c> shape (plan §4 + 00 §7 columns), <c>unique (slug)</c>,
    /// the T4 policy on <c>id</c> and the role grants. The quota columns
    /// (<c>max_projects</c>, <c>max_seats</c>) belong to 02.1.1-T3 and are absent here.
    /// </summary>
    [Fact]
    public async Task OrganizationShape()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.ColumnsAsync(database, "organization")).ShouldBe(
        [
            "id:uuid:uuid:NO:-",
            "name:text:text:NO:-",
            "slug:text:text:NO:-",
            "status:text:text:NO:'active'::text",
            "retention_default_days:integer:int4:YES:-",
            "allowed_email_domains:ARRAY:_text:YES:-",
            "created_at:timestamp with time zone:timestamptz:NO:now()",
            "updated_at:timestamp with time zone:timestamptz:NO:now()",
            "version:integer:int4:NO:1",
        ]);

        var constraints = await _fixture.ConstraintsAsync(database, "organization");
        constraints.ShouldContain("p:organization_pkey:PRIMARY KEY (id)");
        constraints.ShouldContain("u:organization_slug_key:UNIQUE (slug)");
        (await _fixture.ConstraintNamesAsync(database, "organization")).ShouldBe(
        [
            "c:organization_email_domains_check",
            "c:organization_name_length_check",
            "c:organization_slug_format_check",
            "c:organization_status_check",
            "p:organization_pkey",
            "u:organization_slug_key",
        ]);

        // AC1 via the index catalogue: the slug key is a unique btree, and `id` is the key.
        (await _fixture.IndexesAsync(database, "organization")).ShouldBe(
        [
            "organization_pkey:CREATE UNIQUE INDEX organization_pkey ON public.organization USING btree (id)",
            "organization_slug_key:CREATE UNIQUE INDEX organization_slug_key ON public.organization USING btree (slug)",
        ]);

        // AC7: ENABLE + FORCE — the owner is subject to its own policies (DECISIONS F5).
        var rls = await _fixture.RowSecurityAsync(database, "organization");
        rls.Enabled.ShouldBeTrue();
        rls.Forced.ShouldBeTrue();

        // T4: the tenant predicate compares the row's own id, through the NULL-safe helper.
        // No `TO evidgate_operator` policy (TL T76): the grant-gated operator policy is 02.8.1's.
        (await _fixture.PoliciesAsync(database, "organization")).ShouldBe(
        [
            "organization_app_tenant:evidgate_app:*:(id = evidgate.current_org_id()):(id = evidgate.current_org_id())",
            "organization_owner_backfill:evidgate_owner:*:true:true",
        ]);

        await AssertGrantsAsync(database, "organization");
    }

    /// <summary>
    /// T1.2: the <c>project</c> shape — <c>unique (org_id, id)</c> as the
    /// composite-FK target, <c>unique (org_id, slug)</c>, the FK to
    /// <c>organization</c>, the <c>(org_id, status, created_at, id)</c> covering
    /// index of the keyset list query (00 §5.7 P4) and forced row security.
    /// </summary>
    [Fact]
    public async Task ProjectShape()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.ColumnsAsync(database, "project")).ShouldBe(
        [
            "id:uuid:uuid:NO:-",
            "org_id:uuid:uuid:NO:-",
            "name:text:text:NO:-",
            "slug:text:text:NO:-",
            "status:text:text:NO:'active'::text",
            "default_environment_id:uuid:uuid:YES:-",
            "created_at:timestamp with time zone:timestamptz:NO:now()",
            "updated_at:timestamp with time zone:timestamptz:NO:now()",
            "version:integer:int4:NO:1",
        ]);

        var constraints = await _fixture.ConstraintsAsync(database, "project");
        constraints.ShouldContain("p:project_pkey:PRIMARY KEY (id)");
        constraints.ShouldContain("u:project_org_id_key:UNIQUE (org_id, id)");
        constraints.ShouldContain("u:project_org_slug_key:UNIQUE (org_id, slug)");
        constraints.ShouldContain("f:project_organization_fk:FOREIGN KEY (org_id) REFERENCES organization(id)");
        (await _fixture.ConstraintNamesAsync(database, "project")).ShouldBe(
        [
            "c:project_name_length_check",
            "c:project_slug_format_check",
            "c:project_status_check",
            "f:project_organization_fk",
            "p:project_pkey",
            "u:project_org_id_key",
            "u:project_org_slug_key",
        ]);

        (await _fixture.IndexesAsync(database, "project")).ShouldBe(
        [
            "project_org_id_key:CREATE UNIQUE INDEX project_org_id_key ON public.project USING btree (org_id, id)",
            "project_org_slug_key:CREATE UNIQUE INDEX project_org_slug_key ON public.project USING btree (org_id, slug)",
            "project_org_status_created_id_idx:CREATE INDEX project_org_status_created_id_idx ON public.project USING btree (org_id, status, created_at, id)",
            "project_pkey:CREATE UNIQUE INDEX project_pkey ON public.project USING btree (id)",
        ]);

        var rls = await _fixture.RowSecurityAsync(database, "project");
        rls.Enabled.ShouldBeTrue();
        rls.Forced.ShouldBeTrue();

        (await _fixture.PoliciesAsync(database, "project")).ShouldBe(
        [
            "project_app_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            "project_owner_backfill:evidgate_owner:*:true:true",
        ]);

        await AssertGrantsAsync(database, "project");
    }

    /// <summary>
    /// The constraints are not only present but enforced, and the owner-role
    /// insert proves the <c>evidgate_owner</c> policy: with FORCE ROW LEVEL
    /// SECURITY and no policy for the owner, every insert would be rejected with
    /// 42501 (0003 header, rule 6).
    /// </summary>
    [Fact]
    public async Task ConstraintsAndKeysAreEnforced()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        await using (var setup = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await setup.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", orgA),
                ("name", "Acme"),
                ("slug", "acme"));
            await setup.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", orgB),
                ("name", "Globex"),
                ("slug", "globex"));
            await setup.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", Guid.CreateVersion7()),
                ("org_id", orgA),
                ("name", "Checkout"),
                ("slug", "checkout"));
            await setup.CommitAsync();
        }

        // AC1: the global slug key, and the per-org project slug is not global.
        (await InsertAsync(database, OrgInsert(orgA, "Acme2", "acme"))).ShouldNotBeNull()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        (await InsertAsync(database, OrgInsert(orgA, "Acme", "Acme"))).ShouldNotBeNull()
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await InsertAsync(database, OrgInsert(orgA, "Acme", "a"))).ShouldNotBeNull()
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await InsertAsync(database, OrgInsert(orgA, new string('x', 129), "long-name"))).ShouldNotBeNull()
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await InsertAsync(
                database,
                "insert into public.organization (id, name, slug, status) values (@id, 'Acme', 'acme-status', @status)",
                ("id", Guid.CreateVersion7()),
                ("status", "archived")))
            .ShouldNotBeNull().SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        // AC2 at the database level: the same project slug in another org is a
        // different project, the same slug in the same org is a 409.
        (await InsertAsync(database, ProjectInsert(orgB, "checkout"))).ShouldBeNull();
        (await InsertAsync(database, ProjectInsert(orgA, "checkout"))).ShouldNotBeNull()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);

        // The composite tenant FK: a project cannot be created for an unknown org (00 §5.4.1).
        (await InsertAsync(database, ProjectInsert(Guid.CreateVersion7(), "orphan"))).ShouldNotBeNull()
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    /// <summary>
    /// T1.3: the DTO field set is exactly the mapped column set — a column added
    /// by a later migration (or a field invented in the DTO) breaks this test
    /// instead of silently drifting (00 §5.4.5 typed columns, 00 §7 naming).
    /// </summary>
    [Fact]
    public async Task DtoFieldsMatchSchemaColumns()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.ColumnsAsync(database, "organization"))
            .Select(column => column.Split(':')[0])
            .Order(StringComparer.Ordinal)
            .ShouldBe(FieldNames<OrganizationDto>().Order(StringComparer.Ordinal));

        (await _fixture.ColumnsAsync(database, "project"))
            .Select(column => column.Split(':')[0])
            .Order(StringComparer.Ordinal)
            .ShouldBe(FieldNames<ProjectDto>().Order(StringComparer.Ordinal));
    }

    /// <summary>The DTO's wire field names, snake_case (00 §7), for the schema cross-check.</summary>
    private static List<string> FieldNames<T>() =>
        typeof(T)
            .GetProperties()
            .Select(property => DtoContractTests.SnakeCase.PropertyNamingPolicy!.ConvertName(property.Name))
            .ToList();

    /// <summary>
    /// Decision 0001/0003 grants, minus the hard DELETE of M1W3-05: the app role
    /// holds select/insert/update, the operator SELECT only, the sweep role nothing.
    /// </summary>
    private async Task AssertGrantsAsync(string database, string table)
    {
        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, privilege))
                .ShouldBeTrue($"evidgate_app needs {privilege} on {table}");
        }

        // M1W3-05 / 00 §5.4.3: a registry row is never hard-deleted by the app — it
        // is soft-deleted through `status`, so the privilege is revoked even though
        // 0001's default privileges would have granted it.
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "DELETE"))
            .ShouldBeFalse($"evidgate_app must not hold DELETE on {table} (M1W3-05)");

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", table, "SELECT")).ShouldBeTrue();
        foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", table, privilege))
                .ShouldBeFalse($"the audited operator path must not write {table} (TL T76)");
        }

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", table, "SELECT"))
            .ShouldBeFalse("the sweep role holds EXECUTE per function, never table privileges (TL T80)");
    }

    /// <summary>
    /// M1W3-05: the missing DELETE grant is enforced, not decorative — an app-role
    /// <c>delete</c> on either registry table is refused with 42501, so removing an
    /// organization or a project is the <c>status = 'deleted'</c> soft delete of
    /// 02.1.1-T2's commands and never a lost row.
    /// </summary>
    [Fact]
    public async Task AppRoleCannotHardDeleteRegistryRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();

        await using (var setup = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await setup.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, 'Acme', 'acme')",
                ("id", orgId));
            await setup.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, 'Checkout', 'checkout')",
                ("id", projectId),
                ("org_id", orgId));
            await setup.CommitAsync();
        }

        // One scope per statement: a 42501 aborts its transaction, so the second
        // delete needs its own.
        foreach (var (table, id) in new[] { ("organization", orgId), ("project", projectId) })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgId);

            var refused = await scope.CaptureErrorAsync(
                $"delete from public.{table} where id = @id",
                ("id", id));

            refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // The rows are still there: the soft delete is an UPDATE, which the app role does hold.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgId))
        {
            (await scope.ExecuteAsync(
                "update public.organization set status = 'deleted' where id = @id",
                ("id", orgId)))
                .ShouldBe(1);
            (await scope.ExecuteAsync(
                "update public.project set status = 'deleted' where id = @id",
                ("id", projectId)))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.organization o join public.project p on p.org_id = o.id "
            + "where o.status = 'deleted' and p.status = 'deleted'"))
            .ShouldBe(1);
    }

    /// <summary>Runs a statement as the migration role in its own transaction and returns the server error, if any.</summary>
    private async Task<PostgresException?> InsertAsync(
        string database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        try
        {
            await scope.ExecuteAsync(sql, parameters);
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }

    private static string OrgInsert(Guid id, string name, string slug) =>
        $"insert into public.organization (id, name, slug) values ('{id}', '{name}', '{slug}')";

    private static string ProjectInsert(Guid orgId, string slug) =>
        $"insert into public.project (id, org_id, name, slug) values ('{Guid.CreateVersion7()}', '{orgId}', 'Project', '{slug}')";
}
