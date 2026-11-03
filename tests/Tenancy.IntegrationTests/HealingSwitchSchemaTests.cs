using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 08.6.3-T1 SQL level: migration 0052's <c>healing_switch</c>, as the database sees it.
/// <list type="bullet">
/// <item><description>
/// the shape — one row per scope, the closed <c>scope</c>/<c>mode</c> vocabularies,
/// <c>unavailable</c> not storable, the two cross-column CHECKs, the three partial unique keys and
/// the composite project FK (feature plan 08.6.3 §3, AC1/AC2);
/// </description></item>
/// <item><description>
/// the tenant boundary of the NULL-org system row: every tenant reads it, no tenant transaction can
/// write it, and a tenant's own rows stay invisible to every other tenant (AC7, ruling T72);
/// </description></item>
/// <item><description>
/// the operator definer <c>evidgate.healing_switch_set_system</c>: it upserts the one system row, the
/// shared trigger bumps <c>version</c>, an unstorable mode is refused with 22023, a call made from
/// inside a tenant transaction is refused with 23514 and writes nothing, and EXECUTE is held by
/// <c>evidgate_app</c> alone (AC9);
/// </description></item>
/// <item><description>
/// the resolution read's plan: the resolver's SELECT is answered from the three partial unique
/// indexes and sees at most the three rows that can apply (AC9).
/// </description></item>
/// </list>
/// <para>
/// The 27-combination resolution MATRIX and the <c>enabled</c>-while-<c>Healing:Apply</c>-is-off rule
/// are behaviour of the resolver (08.6.3-T2's <c>V1ModeClampTests</c>), not of this DDL; what this
/// file pins is the ordering rule the resolver reads out of the table ("most restrictive stored row
/// wins, and no rows at all resolve to <c>shadow</c>").
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class HealingSwitchSchemaTests : IAsyncLifetime
{
    /// <summary>The operator definer and its exact signature (the one the migration grants EXECUTE on).</summary>
    private const string Definer = "evidgate.healing_switch_set_system(text, text, uuid)";

    private const string Table = "healing_switch";

    private readonly PostgresTenancyFixture _fixture;
    private DedicatedDatabase? _dedicated;

    public HealingSwitchSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // The one system row and the unfiltered counts are per database, so each test owns one.
    public async ValueTask InitializeAsync() => _dedicated = await _fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    /// <summary>Two migrated tenants, each with one project, and the ids that address them.</summary>
    private sealed record World(string Database, Guid OrgA, Guid OrgB, Guid ProjectA, Guid ProjectB);

    private async Task<World> SeedTenantsAsync()
    {
        var database = _dedicated!.Name;
        var world = new World(
            database,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());

        // The organization row's own id IS the tenant (TL T4), so the seed writes the root row with the
        // org id the rest of the test uses — the same write-through-owner path TenantGucTests' seed takes.
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        foreach (var (org, project, slug) in new[]
        {
            (world.OrgA, world.ProjectA, "healing-a"),
            (world.OrgB, world.ProjectB, "healing-b"),
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

    // ------------------------------------------------------------------ one statement per row shape

    /// <summary>The platform-wide row: no org, no project (the operator's, written by the definer).</summary>
    private static Task<int> InsertSystemRowAsync(TenantScope scope, Guid id, string mode, string? reason = null) =>
        scope.ExecuteAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
            values (@id, 'system', null, null, @mode, @reason, @set_by, now())
            """,
            ("id", id),
            ("mode", mode),
            ("reason", (object?)reason ?? DBNull.Value),
            ("set_by", Guid.CreateVersion7()));

    /// <summary>A tenant override: owns an org, no project.</summary>
    private static Task<int> InsertTenantRowAsync(TenantScope scope, Guid id, Guid org, string mode) =>
        scope.ExecuteAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
            values (@id, 'tenant', @org_id, null, @mode, null, @set_by, now())
            """,
            ("id", id),
            ("org_id", org),
            ("mode", mode),
            ("set_by", Guid.CreateVersion7()));

    /// <summary>A project override: owns an org and a project of that org.</summary>
    private static Task<int> InsertProjectRowAsync(
        TenantScope scope,
        Guid id,
        Guid org,
        Guid project,
        string mode) =>
        scope.ExecuteAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
            values (@id, 'project', @org_id, @project_id, @mode, null, @set_by, now())
            """,
            ("id", id),
            ("org_id", org),
            ("project_id", project),
            ("mode", mode),
            ("set_by", Guid.CreateVersion7()));

    /// <summary>Writes the system row through the operator definer, as the app role (the only caller).</summary>
    private async Task<(string Mode, int Version)> SetSystemAsync(World world, string mode)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));
        var stored = await scope.ScalarAsync<string>(
            "select r.mode || ':' || r.version from evidgate.healing_switch_set_system(@mode, @reason, @set_by) r",
            ("mode", mode),
            ("reason", "test"),
            ("set_by", Guid.CreateVersion7()));
        await scope.CommitAsync();

        var parts = stored.Split(':');
        return (parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ (a) shape, keys, RLS flags

    [Fact]
    public async Task Switch_DeclaresTheContractShape()
    {
        var world = await SeedTenantsAsync();

        (await _fixture.TableExistsAsync(world.Database, Table)).ShouldBeTrue();

        var columns = await _fixture.ColumnsAsync(world.Database, Table);
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "scope:text:text:NO:",
            "org_id:uuid:uuid:YES:",
            "project_id:uuid:uuid:YES:",
            "mode:text:text:NO:",
            "reason:text:text:YES:",
            "set_by:uuid:uuid:NO:",
            "at:timestamp with time zone:timestamptz:NO:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            columns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"{Table} needs the 0052 contract column shape '{prefix}'");
        }

        var constraints = await _fixture.ConstraintsAsync(world.Database, Table);

        // TL T3 / 00 §5.4.6: the composite-FK target every business table declares.
        constraints.ShouldContain(
            constraint => constraint.StartsWith("u:healing_switch_org_id_key:", StringComparison.Ordinal)
                && constraint.Contains("org_id, id", StringComparison.Ordinal),
            $"{Table} needs unique (org_id, id) as healing_switch_org_id_key (TL T3)");

        // The composite project key, not a plain project_id -> project (id) one: a project row can only
        // ever name a project of its OWN org.
        constraints.ShouldContain(
            constraint => constraint.StartsWith("f:healing_switch_project_fk:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, project_id)", StringComparison.Ordinal)
                && constraint.Contains("project(org_id, id)", StringComparison.Ordinal),
            "the project reference must be the composite (org_id, project_id) -> project (org_id, id) key");

        foreach (var check in new[]
        {
            "c:healing_switch_scope_check:",
            "c:healing_switch_mode_check:",
            "c:healing_switch_scope_org_check:",
            "c:healing_switch_scope_project_check:",
            "c:healing_switch_reason_length_check:",
            "c:healing_switch_version_check:",
        })
        {
            constraints.ShouldContain(
                constraint => constraint.StartsWith(check, StringComparison.Ordinal),
                $"{Table} needs the CHECK '{check}'");
        }

        // The three partial unique keys carry the row's identity: one system row, one tenant row per
        // org, one project row per project.
        var indexes = await _fixture.IndexesAsync(world.Database, Table);
        indexes.ShouldContain(
            index => index.StartsWith("healing_switch_system_key:", StringComparison.Ordinal)
                && index.Contains("WHERE (scope = 'system'::text)", StringComparison.Ordinal),
            "one system row is the partial unique key healing_switch_system_key");
        indexes.ShouldContain(
            index => index.StartsWith("healing_switch_tenant_key:", StringComparison.Ordinal)
                && index.Contains("(org_id)", StringComparison.Ordinal)
                && index.Contains("WHERE (scope = 'tenant'::text)", StringComparison.Ordinal),
            "one tenant row per org is the partial unique key healing_switch_tenant_key");
        indexes.ShouldContain(
            index => index.StartsWith("healing_switch_project_key:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id)", StringComparison.Ordinal)
                && index.Contains("WHERE (scope = 'project'::text)", StringComparison.Ordinal),
            "one project row per project is the partial unique key healing_switch_project_key");

        var rls = await _fixture.RowSecurityAsync(world.Database, Table);
        rls.Enabled.ShouldBeTrue("DECISIONS F5: every business table is under ENABLE row level security");
        rls.Forced.ShouldBeTrue("DECISIONS F5: FORCE is what makes the owner obey its own policies too");

        // The catalogue read shape (the tenant-less system row) and the tenant write shape, each on the
        // role that must hold it — the boot self-check refuses anything wider.
        var policies = await _fixture.PoliciesAsync(world.Database, Table);
        policies.ShouldContain(
            policy => policy.StartsWith("healing_switch_app_read:evidgate_app:r:", StringComparison.Ordinal)
                && policy.Contains("org_id IS NULL", StringComparison.Ordinal),
            "the app role reads its own rows AND the system row it inherits");
        policies.ShouldContain(
            policy => policy.StartsWith("healing_switch_app_write:evidgate_app:*:", StringComparison.Ordinal)
                && policy.Contains("evidgate.current_org_id()", StringComparison.Ordinal),
            "the app role writes only rows of its own org (polcmd '*' is FOR ALL)");
    }

    // ------------------------------------------------------------------ (b) the closed vocabularies

    [Fact]
    public async Task UnavailableMode_IsNotStorable()
    {
        var world = await SeedTenantsAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA);

        // `unavailable` is a dependency observation (08.6.3 §5), never a configured value: the CHECK
        // refuses it whoever tries, so a caller cannot widen the vocabulary by skipping the route.
        var error = await scope.CaptureErrorAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
            values (@id, 'tenant', @org_id, null, 'unavailable', @set_by, now())
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.OrgA),
            ("set_by", Guid.CreateVersion7()));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("healing_switch_mode_check");
    }

    [Fact]
    public async Task ReasonLength_IsCountedInCodePoints_AndAnEmptyReasonIsStorable()
    {
        var world = await SeedTenantsAsync();

        // `char_length` counts code points, not UTF-16 units, so 2000 astral characters fit — the same unit
        // the route's own 2000-character check uses (HealingModeGate, `EnumerateRunes`). The database is the
        // last line of that bound, not a narrower one.
        var twoThousand = string.Concat(Enumerable.Repeat("\U0001F600", 2000));

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            await scope.ExecuteAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
                values (@id, 'tenant', @org_id, null, 'shadow', @reason, @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("reason", twoThousand),
                ("set_by", Guid.CreateVersion7()));
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<int>(
            world.Database,
            "select char_length(reason) from public.healing_switch where scope = 'tenant'"))
            .ShouldBe(2000, "the column stores 2000 code points, not 2000 UTF-16 units");

        // One code point over the bound is the CHECK's refusal, whoever writes.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
                values (@id, 'project', @org_id, @project_id, 'shadow', @reason, @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("project_id", world.ProjectA),
                ("reason", twoThousand + "\U0001F600"),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_switch_reason_length_check");
        }

        // An empty string passes the CHECK — the database stores "no reason given"; it is the routes'
        // validation that refuses an empty `reason` (422 `reason: empty`), so nothing empty arrives here
        // through them. Pinned so the migration comment and the behaviour cannot drift apart.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await scope.ExecuteAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
                values (@id, 'project', @org_id, @project_id, 'shadow', '', @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("project_id", world.ProjectA),
                ("set_by", Guid.CreateVersion7()));
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            """
            select bool_or(reason = '' and reason is not null)
              from public.healing_switch
             where scope = 'project'
            """))
            .ShouldBeTrue("the CHECK bounds the length only; an empty reason is storable");
    }

    [Fact]
    public async Task UnknownScope_IsNotStorable()
    {
        var world = await SeedTenantsAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        var error = await scope.CaptureErrorAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
            values (@id, 'org', @org_id, null, 'shadow', @set_by, now())
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.OrgA),
            ("set_by", Guid.CreateVersion7()));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("healing_switch_scope_check");
    }

    [Fact]
    public async Task ScopeAndOrgId_MustAgree()
    {
        var world = await SeedTenantsAsync();

        // A system row belongs to no tenant, and every other scope names its org. Both halves are CHECKs,
        // so they hold for the owner too — RLS is a second line, not the only one.
        (string Sql, string ExpectedConstraint, object OrgParameter)[] cases =
        [
            (
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'system', @org_id, null, 'shadow', @set_by, now())
                """,
                "healing_switch_scope_org_check",
                world.OrgA),
            (
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'tenant', null, null, 'shadow', @set_by, now())
                """,
                "healing_switch_scope_org_check",
                DBNull.Value),
        ];

        foreach (var (sql, expectedConstraint, orgParameter) in cases)
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                sql,
                ("id", Guid.CreateVersion7()),
                ("org_id", orgParameter),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe(expectedConstraint);
        }
    }

    [Fact]
    public async Task ProjectId_ExistsExactlyForTheProjectScope()
    {
        var world = await SeedTenantsAsync();

        // A project row without a project has no addressee …
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'project', @org_id, null, 'shadow', @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_switch_scope_project_check");
        }

        // … and a tenant row must not carry one, because that would be a second project identity.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'tenant', @org_id, @project_id, 'shadow', @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("project_id", world.ProjectA),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_switch_scope_project_check");
        }
    }

    [Fact]
    public async Task PartialUniqueKeys_RefuseASecondRowPerScope()
    {
        var world = await SeedTenantsAsync();

        // The system row: the operator definer's upsert and the resolver's "one value per scope" both
        // rely on exactly one row existing.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await InsertSystemRowAsync(owner, Guid.CreateVersion7(), "shadow");
            var error = await owner.CaptureErrorAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'system', null, null, 'shadow', @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("healing_switch_system_key");
        }

        // One tenant override per org.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await InsertTenantRowAsync(owner, Guid.CreateVersion7(), world.OrgA, "shadow");
            var error = await owner.CaptureErrorAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'tenant', @org_id, null, 'disabled', @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("healing_switch_tenant_key");
        }

        // One project override per project — and the same project under another org is NOT the same key
        // (the index is (org_id, project_id)), which is what keeps the two overrides distinguishable.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await InsertProjectRowAsync(owner, Guid.CreateVersion7(), world.OrgA, world.ProjectA, "shadow");
            var error = await owner.CaptureErrorAsync(
                """
                insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
                values (@id, 'project', @org_id, @project_id, 'disabled', @set_by, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("project_id", world.ProjectA),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("healing_switch_project_key");
        }
    }

    // ------------------------------------------------------------------ (c) the tenant boundary

    [Fact]
    public async Task AppRole_CannotInsertTheSystemRow_WithCheck()
    {
        var world = await SeedTenantsAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA);

        // The row is well formed apart from its tenant: a NULL org_id can never equal
        // evidgate.current_org_id(), so only the policy's WITH CHECK can refuse it — and it does.
        var error = await scope.CaptureErrorAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
            values (@id, 'system', null, null, 'disabled', @set_by, now())
            """,
            ("id", Guid.CreateVersion7()),
            ("set_by", Guid.CreateVersion7()));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.ShouldContain("row-level security");
    }

    [Fact]
    public async Task TenantTransaction_CannotWriteTheSystemRow()
    {
        var world = await SeedTenantsAsync();
        await SetSystemAsync(world, "enabled");

        // An UPDATE of the system row is not refused, it simply matches no row: the write policy's USING
        // is the tenant predicate and the row's org_id is NULL.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync("update public.healing_switch set mode = 'disabled' where scope = 'system'"))
                .ShouldBe(0, "a tenant transaction must not reach the tenant-less system row");
        }

        // There is no DELETE grant at all, so the same attempt is a privilege error rather than a no-op.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.CaptureErrorAsync("delete from public.healing_switch where scope = 'system'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select mode from public.healing_switch where scope = 'system'"))
            .ShouldBe("enabled", "the system row is untouched by the tenant's attempts");
    }

    [Fact]
    public async Task Tenants_SeeTheirOwnRowsAndTheSystemRow_AndNothingElse()
    {
        var world = await SeedTenantsAsync();
        await SetSystemAsync(world, "shadow");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            await InsertTenantRowAsync(scope, Guid.CreateVersion7(), world.OrgA, "disabled");
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            await InsertProjectRowAsync(scope, Guid.CreateVersion7(), world.OrgB, world.ProjectB, "enabled");
            await scope.CommitAsync();
        }

        // Org A sees its own tenant row plus the shared system row.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ScalarAsync<string>(
                "select string_agg(scope || ':' || mode, ',' order by scope) from public.healing_switch"))
                .ShouldBe("system:shadow,tenant:disabled");

            // Org B's project row is invisible — and an UPDATE aimed at it matches nothing.
            (await scope.ExecuteAsync(
                "update public.healing_switch set mode = 'disabled' where scope = 'project'"))
                .ShouldBe(0, "another tenant's row must not be reachable by an UPDATE");
        }

        // Org B sees its own project row plus the same system row.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            (await scope.ScalarAsync<string>(
                "select string_agg(scope || ':' || mode, ',' order by scope) from public.healing_switch"))
                .ShouldBe("project:enabled,system:shadow");
        }
    }

    [Fact]
    public async Task AppRole_CanWriteOnlyTheSixSwitchColumns()
    {
        var world = await SeedTenantsAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            await InsertTenantRowAsync(scope, Guid.CreateVersion7(), world.OrgA, "enabled");
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync(
                """
                update public.healing_switch
                   set mode = 'disabled', reason = 'incident', set_by = @set_by, at = now()
                 where scope = 'tenant'
                """,
                ("set_by", Guid.CreateVersion7())))
                .ShouldBe(1, "the granted UPDATE set is exactly what a switch change re-stamps");
        }

        // The row's identity is outside the column grant: 42501 before any policy or CHECK runs.
        foreach (var set in new[]
        {
            $"scope = 'system'",
            $"org_id = '{Guid.CreateVersion7()}'",
            "project_id = null",
            $"id = '{Guid.CreateVersion7()}'",
            "created_at = now()",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA);
            (await scope.CaptureErrorAsync($"update public.healing_switch set {set} where scope = 'tenant'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, set);
        }
    }

    [Fact]
    public async Task AppRole_InsertsOnlyTheEightSwitchColumns()
    {
        var world = await SeedTenantsAsync();

        // The grant is column-level on exactly the eight columns a switch row is made of (CR: a
        // table-level INSERT would let a caller name `created_at`, `updated_at` or `version` and backdate
        // a switch change or pin its concurrency token). `has_table_privilege` stays false for a
        // column-level grant, so both halves are asserted.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select has_table_privilege('evidgate_app', 'public.{Table}', 'INSERT')"))
            .ShouldBeFalse("INSERT is granted per column, never on the table");

        foreach (var column in new[] { "id", "scope", "org_id", "project_id", "mode", "reason", "set_by", "at" })
        {
            (await _fixture.ScalarAsync<bool>(
                world.Database,
                $"select has_column_privilege('evidgate_app', 'public.{Table}', '{column}', 'INSERT')"))
                .ShouldBeTrue($"the app role inserts `{column}`");
        }

        foreach (var column in new[] { "created_at", "updated_at", "version" })
        {
            (await _fixture.ScalarAsync<bool>(
                world.Database,
                $"select has_column_privilege('evidgate_app', 'public.{Table}', '{column}', 'INSERT')"))
                .ShouldBeFalse($"`{column}` belongs to the column defaults and the trigger, not to the caller");
        }

        // … and naming one of them is refused before any policy or CHECK runs.
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA);
        var error = await scope.CaptureErrorAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at, version)
            values (@id, 'tenant', @org_id, null, 'shadow', @set_by, now(), 7)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.OrgA),
            ("set_by", Guid.CreateVersion7()));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task CompositeFk_RefusesAProjectOfAnotherTenant()
    {
        var world = await SeedTenantsAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB);

        // Filed under org B (so the WITH CHECK passes) while naming org A's project: only the composite
        // foreign key can refuse it, which is exactly why the key carries org_id.
        var error = await scope.CaptureErrorAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at)
            values (@id, 'project', @org_id, @project_id, 'shadow', @set_by, now())
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.OrgB),
            ("project_id", world.ProjectA),
            ("set_by", Guid.CreateVersion7()));

        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.ShouldBe("healing_switch_project_fk");
    }

    [Fact]
    public async Task ProjectScope_RowDiesWithItsProject()
    {
        var world = await SeedTenantsAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            await InsertProjectRowAsync(scope, Guid.CreateVersion7(), world.OrgA, world.ProjectA, "disabled");
            await scope.CommitAsync();
        }

        // The composite FK cascades on delete (the 08.6.3 shared contract): a project override must not
        // outlive its project and block the 06.4.4 erasure's hard delete with a 23503. The hard delete
        // is an owner operation — the app role holds no DELETE on `project` either.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync("delete from public.project where id = @id", ("id", world.ProjectA)))
                .ShouldBe(1);
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.healing_switch"))
            .ShouldBe(0, "the project-scope row died with its project");
    }

    // ------------------------------------------------------------------ (d) the operator definer
    [Fact]
    public async Task Definer_UpsertsTheSystemRow_AndTheTriggerBumpsVersion()
    {
        var world = await SeedTenantsAsync();

        var first = await SetSystemAsync(world, "shadow");
        first.Mode.ShouldBe("shadow");
        first.Version.ShouldBe(1, "the create branch stores version 1 (the 00 §7 default)");

        var second = await SetSystemAsync(world, "disabled");
        second.Mode.ShouldBe("disabled");
        second.Version.ShouldBe(2, "every later call is an UPDATE, so the shared trigger bumps the version");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.healing_switch where scope = 'system'"))
            .ShouldBe(1, "the definer UPSERTs one row, never a second");
    }

    [Fact]
    public async Task Definer_RefusesATenantTransaction_AndLeavesEveryRowAlone()
    {
        var world = await SeedTenantsAsync();
        await SetSystemAsync(world, "enabled");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            await InsertTenantRowAsync(scope, Guid.CreateVersion7(), world.OrgA, "enabled");
            await InsertProjectRowAsync(scope, Guid.CreateVersion7(), world.OrgA, world.ProjectA, "enabled");
            await scope.CommitAsync();
        }

        var before = await AllRowsAsync(world.Database);

        // The definer borrows `evidgate_owner`'s rights, so row level security cannot confine it: the
        // function refuses on its own account when a tenant context is set — before the argument checks
        // and before any statement — and the refusal is the same for every org. It is a 23514 (wrong
        // writer/context, 0027/0030 precedent), not a 42501: the caller does hold EXECUTE.
        foreach (var org in new[] { world.OrgA, world.OrgB })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
            var error = await scope.CaptureErrorAsync(
                "select * from evidgate.healing_switch_set_system('disabled', 'operator', @set_by)",
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(
                PostgresErrorCodes.CheckViolation,
                "a tenant transaction must not reach the platform-wide switch through the definer");
            error.MessageText.ShouldContain("tenant transaction");
        }

        (await AllRowsAsync(world.Database)).ShouldBe(
            before,
            "a refused call writes nothing: no system, tenant or project row changes, not even its version");

        // Outside a tenant context the call still works — the app role on the operator route's own
        // connection (no org GUC) …

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database)))
        {
            (await app.ScalarAsync<string>(
                "select r.mode || ':' || r.version from evidgate.healing_switch_set_system('disabled', 'operator', @set_by) r",
                ("set_by", Guid.CreateVersion7())))
                .ShouldBe("disabled:2", "the first update of the seeded row bumps version 1 -> 2");
            await app.CommitAsync();
        }

        // … and as the owner, which is how a migration or a backfill would call it.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ScalarAsync<string>(
                "select r.mode || ':' || r.version from evidgate.healing_switch_set_system('shadow', null, @set_by) r",
                ("set_by", Guid.CreateVersion7())))
                .ShouldBe("shadow:3");
            await owner.CommitAsync();
        }

        (await AllRowsAsync(world.Database)).ShouldBe(
            "project:enabled:1:-,system:shadow:3:-,tenant:enabled:1:-",
            "the definer writes the system row and nothing else, at any scope");
    }

    /// <summary>
    /// Every switch row as <c>scope:mode:version:reason</c> (<c>-</c> for a null reason), scope-ordered — a
    /// whole-table snapshot, so a refusal can prove that nothing anywhere moved.
    /// </summary>
    private async Task<string> AllRowsAsync(string database) =>
        await _fixture.ScalarAsync<string>(
            database,
            """
            select coalesce(
                     string_agg(
                       scope || ':' || mode || ':' || version || ':' || coalesce(reason, '-'),
                       ',' order by scope),
                     '')
              from public.healing_switch
            """);

    [Fact]
    public async Task Definer_RefusesAModeOutsideTheThree()
    {
        var world = await SeedTenantsAsync();

        // `unavailable` is the one value the wire vocabulary names and the database refuses: it is an
        // observation of a dependency, not a configuration.
        foreach (var mode in new[] { "unavailable", "bogus", "UNAVAILABLE", "" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                "select * from evidgate.healing_switch_set_system(@mode, null, @set_by)",
                ("mode", mode),
                ("set_by", Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue, mode);
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                "select * from evidgate.healing_switch_set_system(null, null, @set_by)",
                ("set_by", Guid.CreateVersion7()));
            error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.healing_switch"))
            .ShouldBe(0, "a refused call writes nothing");
    }

    [Fact]
    public async Task Definer_IsExecutableByTheAppRoleAlone()
    {
        var world = await SeedTenantsAsync();

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select has_function_privilege('evidgate_app', '{Definer}', 'EXECUTE')"))
            .ShouldBeTrue("the operator ingress runs as evidgate_app (02.8.1 RequireOperatorFilter)");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await _fixture.ScalarAsync<bool>(
                world.Database,
                $"select has_function_privilege('{role}', '{Definer}', 'EXECUTE')"))
                .ShouldBeFalse($"{role} must not set the platform-wide switch");
        }

        // The built-in PUBLIC grant is closed (TL T80), the definition is SECURITY DEFINER, and its
        // search_path is pinned empty so no caller can steer resolution of an unqualified name.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            """
            select count(*)
              from pg_catalog.pg_proc p
             cross join lateral aclexplode(p.proacl) a
             where p.oid = 'evidgate.healing_switch_set_system(text, text, uuid)'::regprocedure
               and a.grantee = 0
            """))
            .ShouldBe(0, "EXECUTE must never be granted to PUBLIC");

        // `proconfig` may spell the empty value as `search_path=""` (it does on the pinned
        // postgres:18.6), so the setting is compared quote-stripped, as Identity's 0051 test does.
        var settings = (await _fixture.ScalarAsync<string>(
                world.Database,
                """
                select array_to_string(proconfig, ',')
                  from pg_catalog.pg_proc
                 where oid = 'evidgate.healing_switch_set_system(text, text, uuid)'::regprocedure
                """))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split('=', 2))
            .Select(parts => (Name: parts[0], Value: parts.Length > 1 ? parts[1].Trim('"', '\'') : string.Empty))
            .ToArray();

        settings.Select(setting => setting.Name).ShouldBe(
            ["search_path"],
            "SECURITY DEFINER pinned to the empty search_path and nothing else (0051 precedent)");
        settings[0].Value.ShouldBeEmpty("`search_path` is pinned to the empty string");

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            """
            select prosecdef
              from pg_catalog.pg_proc
             where oid = 'evidgate.healing_switch_set_system(text, text, uuid)'::regprocedure
            """)).ShouldBeTrue("the system row is only reachable as the owner, so the writer must be SECURITY DEFINER");
    }

    // ------------------------------------------------------------------ (e) the resolution read

    [Fact]
    public async Task ResolutionRead_SeesAtMostTheThreeRowsThatCanApply()
    {
        var world = await SeedTenantsAsync();
        await SetSystemAsync(world, "enabled");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            await InsertTenantRowAsync(scope, Guid.CreateVersion7(), world.OrgA, "shadow");
            await InsertProjectRowAsync(scope, Guid.CreateVersion7(), world.OrgA, world.ProjectA, "disabled");
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            // The resolver's read: the system row, the org's tenant row and the project's row — no more,
            // however many rows the table holds (three partial unique keys bound it).
            (await scope.ScalarAsync<int>(
                """
                select count(*)::int
                  from public.healing_switch h
                 where h.scope = 'system'
                    or h.org_id = evidgate.current_org_id()
                """))
                .ShouldBe(3, "at most one row per scope can apply to one resolution");
        }

        // Most restrictive wins (disabled > shadow > enabled), and with no row at all the resolver's
        // documented V1 posture applies: shadow, never `enabled`.
        (await ResolveAsync(world, world.OrgA, world.ProjectA)).ShouldBe("disabled");

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await owner.ExecuteAsync("delete from public.healing_switch");
            await owner.CommitAsync();
        }

        (await ResolveAsync(world, world.OrgA, world.ProjectA))
            .ShouldBe("shadow", "a database with no row at any scope resolves to the V1 shadow posture");
    }

    [Fact]
    public async Task ResolutionRead_TakesTheMostRestrictiveStoredRow()
    {
        var world = await SeedTenantsAsync();

        // Every combination of the three scopes' stored modes (absent is a value of its own), with the
        // expectation written out — the ordering rule the resolver reads out of this table, pinned rather
        // than re-derived: disabled > shadow > enabled, absent rows ignored, all absent -> shadow.
        var cases = new (string? System, string? Tenant, string? Project, string Expected)[]
        {
            (null, null, null, "shadow"),
            ("enabled", null, null, "enabled"),
            ("shadow", null, null, "shadow"),
            ("disabled", null, null, "disabled"),
            (null, "enabled", null, "enabled"),
            (null, "shadow", null, "shadow"),
            (null, "disabled", null, "disabled"),
            (null, null, "enabled", "enabled"),
            (null, null, "shadow", "shadow"),
            (null, null, "disabled", "disabled"),
            ("enabled", "enabled", "enabled", "enabled"),
            ("enabled", "enabled", "shadow", "shadow"),
            ("enabled", "enabled", "disabled", "disabled"),
            ("enabled", "shadow", "enabled", "shadow"),
            ("enabled", "shadow", "shadow", "shadow"),
            ("enabled", "shadow", "disabled", "disabled"),
            ("enabled", "disabled", "enabled", "disabled"),
            ("enabled", "disabled", "shadow", "disabled"),
            ("enabled", "disabled", "disabled", "disabled"),
            ("shadow", "enabled", "enabled", "shadow"),
            ("disabled", "enabled", "enabled", "disabled"),
            ("shadow", "disabled", "enabled", "disabled"),
            ("disabled", "shadow", "enabled", "disabled"),
            ("shadow", "enabled", "disabled", "disabled"),
            ("disabled", "enabled", "shadow", "disabled"),
            (null, "enabled", "disabled", "disabled"),
            ("disabled", "enabled", null, "disabled"),
        };

        foreach (var (systemMode, tenantMode, projectMode, expected) in cases)
        {
            await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
            {
                await owner.ExecuteAsync("delete from public.healing_switch");
                if (systemMode is not null)
                {
                    await InsertSystemRowAsync(owner, Guid.CreateVersion7(), systemMode);
                }

                if (tenantMode is not null)
                {
                    await InsertTenantRowAsync(owner, Guid.CreateVersion7(), world.OrgA, tenantMode);
                }

                if (projectMode is not null)
                {
                    await InsertProjectRowAsync(owner, Guid.CreateVersion7(), world.OrgA, world.ProjectA, projectMode);
                }

                await owner.CommitAsync();
            }

            (await ResolveAsync(world, world.OrgA, world.ProjectA)).ShouldBe(
                expected,
                $"system={systemMode ?? "absent"}, tenant={tenantMode ?? "absent"}, project={projectMode ?? "absent"}");
        }
    }

    /// <summary>
    /// The resolver's SELECT, run as the app role of <paramref name="org"/>: the rows this org can see
    /// (the system row, its own tenant row, one project row), most restrictive first, `shadow` when none
    /// exists. Written here so the ordering rule the migration's keys make cheap is asserted on the real
    /// database, not only in the resolver's unit tests.
    /// </summary>
    private async Task<string> ResolveAsync(World world, Guid org, Guid project)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org, project);
        return await scope.ScalarAsync<string>(
            """
            select coalesce(
                     (select h.mode
                        from public.healing_switch h
                       where h.scope = 'system'
                          or (h.org_id = evidgate.current_org_id() and h.scope = 'tenant')
                          or (h.org_id = evidgate.current_org_id()
                              and h.scope = 'project'
                              and h.project_id = evidgate.current_project_id())
                       order by case h.mode when 'disabled' then 0 when 'shadow' then 1 else 2 end
                       limit 1),
                     'shadow')
            """);
    }
}
