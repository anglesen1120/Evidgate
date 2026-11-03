using System.Globalization;
using System.IO;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Migrator;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T1.1 against a real PostgreSQL: migration 0039's authoring tree — the five tables with
/// their tenant keys and composite tenant FKs, the declared indexes, FORCE RLS with the tenant and
/// owner policies, and the <c>project.case_prefix</c> contract (stem/dedupe backfill, the per-org
/// unique index, the format check and the insert default).
/// <para>
/// The fixture is this suite's own <see cref="PostgresTenancyFixture"/> — one digest-pinned
/// PostgreSQL 18 container per assembly, a fresh database per test brought to head by the real
/// <c>Evidgate.Migrator</c> (the 05.2.1-T1 / 05.3.3-T1 precedent for a new table in this suite).
/// The two backfill cases go one step further: they bring a database to the **previous** head
/// (every file lexically before 0039), seed the rows the backfill has to name, and only then apply
/// 0039 — the deploy path, not a from-scratch run.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringSchemaTests
{
    /// <summary>The migration under test; the backfill cases stop just before it.</summary>
    private const string MigrationFile = "0039_authoring_tree.sql";

    /// <summary>The case-code prefix grammar of OI-13-101 (<c>project_case_prefix_format</c>).</summary>
    private const string PrefixFormat = "^[A-Z][A-Z0-9]{1,7}$";

    /// <summary>Every table migration 0039 creates, in dependency order.</summary>
    private static readonly string[] Tables =
    [
        "feature_node",
        "checklist_item",
        "manual_test_case",
        "authoring_code_counter",
        "authoring_version",
    ];

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ shape: keys, FKs, indexes, columns

    /// <summary>
    /// 00 §5.4.1/§5.4.6 (TL T3): every table carries <c>unique (org_id, id)</c> — the only target a
    /// composite tenant FK may reference — every same-project table adds
    /// <c>unique (org_id, project_id, id)</c>, and **every** foreign key of the five tables is
    /// composite and leads with <c>org_id</c>, so no row can point at another tenant's.
    /// </summary>
    [Fact]
    public async Task TenancyKeys_And_CompositeForeignKeys()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        foreach (var table in Tables)
        {
            var constraints = await _fixture.ConstraintsAsync(database, table);

            // `contype:conname:definition`, e.g. `u:authoring_version_org_id_key:UNIQUE (org_id, id)`.
            constraints.ShouldContain(
                entry => entry.StartsWith("u:" + table + "_org_id_key:", StringComparison.Ordinal)
                    && entry.EndsWith("(org_id, id)", StringComparison.Ordinal),
                $"{table} must declare unique (org_id, id) — the composite-FK target of TL T3");

            foreach (var foreignKey in constraints.Where(entry => entry.StartsWith("f:", StringComparison.Ordinal)))
            {
                foreignKey.ShouldContain(
                    "FOREIGN KEY (org_id,",
                    customMessage: $"{table}: every tenant FK leads with org_id — {foreignKey}");
            }
        }

        // The same-project composite keys: the three entity tables.
        var projectKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["feature_node"] = "feature_node_project_id_key",
            ["checklist_item"] = "checklist_item_project_id_key",
            ["manual_test_case"] = "manual_test_case_project_id_key",
        };

        foreach (var (table, name) in projectKeys)
        {
            (await _fixture.ConstraintsAsync(database, table))
                .ShouldContain(entry => entry == "u:" + name + ":UNIQUE (org_id, project_id, id)");
        }

        // The parent of a node is pinned to the same project by its own composite key, and never to
        // the node itself; the case table's natural key is the one an idempotent re-import conflicts
        // on (AC15), and the case code carries the prefix grammar.
        (await _fixture.ConstraintsAsync(database, "feature_node"))
            .ShouldContain(entry => entry.StartsWith("f:feature_node_parent_fk:", StringComparison.Ordinal)
                && entry.Contains("FOREIGN KEY (org_id, project_id, parent_id) REFERENCES", StringComparison.Ordinal)
                && entry.Contains("feature_node(org_id, project_id, id)", StringComparison.Ordinal));
        (await _fixture.ConstraintsAsync(database, "feature_node"))
            .ShouldContain(entry => entry.StartsWith("c:feature_node_parent_not_self:", StringComparison.Ordinal));
        (await _fixture.ConstraintsAsync(database, "manual_test_case"))
            .ShouldContain(entry => entry == "u:manual_test_case_code_key:UNIQUE (org_id, project_id, code)");
        (await _fixture.ConstraintsAsync(database, "manual_test_case"))
            .ShouldContain(entry => entry.StartsWith("c:manual_test_case_code_check:", StringComparison.Ordinal)
                && entry.Contains("[A-Z][A-Z0-9]{1,7}-[0-9]{3,10}", StringComparison.Ordinal));
        (await _fixture.ConstraintsAsync(database, "manual_test_case"))
            .ShouldContain(entry => entry.StartsWith("c:manual_test_case_steps_check:", StringComparison.Ordinal)
                && entry.Contains("jsonb_typeof(steps)", StringComparison.Ordinal));

        // The version log: the tenant key, one version number per entity, and the nullable author FK.
        (await _fixture.ConstraintsAsync(database, "authoring_version"))
            .ShouldContain(entry => entry == "u:authoring_version_entity_key:UNIQUE (org_id, project_id, entity_kind, entity_id, version_no)");
        (await _fixture.ConstraintsAsync(database, "authoring_version"))
            .ShouldContain(entry => entry.Contains("FOREIGN KEY (org_id, author_membership_id) REFERENCES", StringComparison.Ordinal)
                && entry.Contains("membership(org_id, id)", StringComparison.Ordinal));

        // The counter's natural key is the UPSERT target the allocator writes; the surrogate id is
        // what satisfies the tenant-key rule (the 0033 `policy_activation` precedent).
        (await _fixture.ConstraintsAsync(database, "authoring_code_counter"))
            .ShouldContain(entry => entry == "p:authoring_code_counter_pkey:PRIMARY KEY (org_id, project_id)");
        (await _fixture.ConstraintsAsync(database, "authoring_code_counter"))
            .ShouldContain(entry => entry.StartsWith("c:authoring_code_counter_next_value_check:", StringComparison.Ordinal)
                && entry.Contains("<= 1000000001", StringComparison.Ordinal));
    }

    /// <summary>
    /// The indexes that decide behaviour exist with the shape plan §4 declares (P4): the partial
    /// sibling index that makes `position` unique per live parent (`nulls not distinct`), the two
    /// keyset orders and the per-org prefix index. <c>pg_get_indexdef</c> quotes the keyword
    /// <c>"position"</c>, so the comparison runs on the definition with quotes removed.
    /// </summary>
    [Fact]
    public async Task DeclaredIndexes_Exist()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        // Live siblings only, `nulls not distinct` so two NULL-parented roots compare equal.
        var nodeIndexes = (await _fixture.IndexesAsync(database, "feature_node")).Select(Unquoted).ToList();
        nodeIndexes.ShouldContain(entry => entry.StartsWith("feature_node_sibling_position_uq:", StringComparison.Ordinal)
            && entry.Contains("org_id, project_id, parent_id, position", StringComparison.Ordinal)
            && entry.Contains("NULLS NOT DISTINCT", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("deleted_at IS NULL", StringComparison.OrdinalIgnoreCase));
        nodeIndexes.ShouldContain(entry => entry.StartsWith("feature_node_tree_idx:", StringComparison.Ordinal)
            && entry.Contains("org_id, project_id, parent_id, position, id", StringComparison.Ordinal));

        (await _fixture.IndexesAsync(database, "checklist_item")).Select(Unquoted)
            .ShouldContain(entry => entry.StartsWith("checklist_item_list_idx:", StringComparison.Ordinal)
                && entry.Contains("org_id, project_id, feature_node_id, state, position, id", StringComparison.Ordinal));

        (await _fixture.IndexesAsync(database, "manual_test_case")).Select(Unquoted)
            .ShouldContain(entry => entry.StartsWith("manual_test_case_list_idx:", StringComparison.Ordinal)
                && entry.Contains("org_id, project_id, checklist_item_id, state, code", StringComparison.Ordinal));

        var versionIndexes = (await _fixture.IndexesAsync(database, "authoring_version")).Select(Unquoted).ToList();
        versionIndexes.ShouldContain(entry => entry.StartsWith("authoring_version_timeline_idx:", StringComparison.Ordinal)
            && entry.Contains("created_at DESC", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("id DESC", StringComparison.OrdinalIgnoreCase));
        versionIndexes.ShouldContain(entry => entry.StartsWith("authoring_version_batch_idx:", StringComparison.Ordinal)
            && entry.Contains("org_id, project_id, batch_id", StringComparison.Ordinal));

        // The project side: the per-org prefix index the backfill is checked by.
        (await _fixture.IndexesAsync(database, "project")).Select(Unquoted)
            .ShouldContain(entry => entry.StartsWith("project_org_case_prefix_uq:", StringComparison.Ordinal)
                && entry.Contains("org_id, case_prefix", StringComparison.Ordinal));
    }

    /// <summary>
    /// The entity tables grant UPDATE per column (0027:159, 0033:94 precedent): the app sets the
    /// mutable columns its statements name, while a statement that names an identity column is
    /// refused at the ACL with 42501 — before RLS and before any trigger sees the row.
    /// </summary>
    [Fact]
    public async Task ImmutableColumns_Update_RefusedForApp()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var node = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seed.ExecuteAsync(InsertNode(node, org, project, null, "module", "Shop", 0));
            await seed.CommitAsync();
        }

        // One scope per statement: the error aborts the whole transaction, which would answer 25P02
        // (current transaction is aborted) instead of telling the ACL refusal from the next one.
        await AssertRefusedAsync("update public.feature_node set org_id = @value where id = @id", ("value", Guid.CreateVersion7()));
        await AssertRefusedAsync("update public.feature_node set project_id = @value where id = @id", ("value", Guid.CreateVersion7()));
        await AssertRefusedAsync("update public.feature_node set id = @value where id = @id", ("value", Guid.CreateVersion7()));
        await AssertRefusedAsync("update public.feature_node set created_at = clock_timestamp() where id = @id");

        // … while the mutable columns go through, which is what makes the refusals above the column
        // grant and not a broken statement shape.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await app.ExecuteAsync(
                "update public.feature_node set title = @title, position = 1 where id = @id",
                ("title", "Shop, renamed"),
                ("id", node))).ShouldBe(1, "the mutable columns are granted");
            await app.CommitAsync();
        }

        async Task AssertRefusedAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var attempt = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            (await attempt.CaptureErrorAsync(sql, [.. parameters, ("id", node)])).SqlState
                .ShouldBe("42501", $"an identity column is not grantable: {sql}");
        }
    }

    // The five tables and `project.case_prefix` are pinned where they are *decided*: the column
    // shape by `OrgProjectSchemaTests.ProjectShape` and `AuthoringSchemaTests.TenancyKeys_…`, the
    // audit columns by `SchemaConventionTests.Migrated_Schema_Satisfies_Conventions`.

    // ------------------------------------------------------------------ RLS

    /// <summary>
    /// 00 §5.4 rule 7 / DECISIONS F5 (TL T2): ENABLE **and** FORCE row level security on every new
    /// table, exactly two policies — the app tenant policy over <c>evidgate.current_org_id()</c> and
    /// the permissive owner policy, without which the owner backfill would update zero rows.
    /// </summary>
    [Fact]
    public async Task ForceRls_AndOwnerPolicyOnEveryTable()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        foreach (var table in Tables)
        {
            (await _fixture.RowSecurityAsync(database, table))
                .ShouldBe((true, true), $"{table}: ENABLE + FORCE row level security");

            (await _fixture.PoliciesAsync(database, table)).ShouldBe(
            [
                $"{table}_owner_backfill:evidgate_owner:*:true:true",
                $"{table}_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            ]);
        }
    }

    // ------------------------------------------------------------------ sibling positions (AC2)

    /// <summary>
    /// AC2 without the API: two live rows with one <c>position</c> are refused — for children of one
    /// parent **and** for two NULL-parented roots (<c>nulls not distinct</c>) — while a tombstone
    /// keeps its old position and does not block a new node.
    /// </summary>
    [Fact]
    public async Task SiblingPosition_UniqueForRootAndChild()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var root = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seed.ExecuteAsync(InsertNode(root, org, project, null, "module", "Shop", 0));
            // A child at position 0 is independent of the root's position 0: different parent.
            await seed.ExecuteAsync(InsertNode(Guid.CreateVersion7(), org, project, root, "feature", "Cart", 0));
            await seed.CommitAsync();
        }

        // A second live root at position 0: the NULL parent of two roots must compare equal.
        var duplicateRoot = await CaptureOwnerErrorAsync(
            database,
            InsertNode(Guid.CreateVersion7(), org, project, null, "module", "Other", 0));
        duplicateRoot.SqlState.ShouldBe("23505");
        duplicateRoot.ConstraintName.ShouldBe("feature_node_sibling_position_uq");

        // A second live child of the same parent at position 0.
        var duplicateChild = await CaptureOwnerErrorAsync(
            database,
            InsertNode(Guid.CreateVersion7(), org, project, root, "feature", "Wishlist", 0));
        duplicateChild.SqlState.ShouldBe("23505");
        duplicateChild.ConstraintName.ShouldBe("feature_node_sibling_position_uq");

        // Soft-delete the root: its position stays, and the freed number accepts a new live root.
        await using (var tombstone = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            (await tombstone.ExecuteAsync(
                "update public.feature_node set deleted_at = now() where id = @id",
                ("id", root))).ShouldBe(1);
            await tombstone.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.feature_node where id = '{root}' and position = 0 and deleted_at is not null"))
            .ShouldBe(1, "a tombstone keeps its old position");

        await using (var reinsert = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            (await reinsert.ExecuteAsync(InsertNode(Guid.CreateVersion7(), org, project, null, "module", "Other", 0)))
                .ShouldBe(1);
            await reinsert.CommitAsync();
        }
    }

    // ------------------------------------------------------------------ case prefix: backfill (AC4)

    /// <summary>
    /// AC4/§4: every project gets a non-NULL, format-valid prefix that is unique **within its org**
    /// (the same value twice in two orgs is fine), assigned by the backfill of 0039 against a
    /// database seeded before the migration ran.
    /// </summary>
    [Fact]
    public async Task CasePrefix_Backfill_IsUniqueAndWellFormed()
    {
        await using var database = await BringToPreviousHeadAsync();
        var first = await SeedOrgAsync(database, "acme");
        var second = await SeedOrgAsync(database, "globex");

        // Distinct stems, seeded before 0039 exists: `shop-web` -> SHOP, `flashcards` -> FLAS,
        // `ab` -> AB, `123-shop` -> P123S (the plan's own derivation examples).
        var slugs = new[] { "shop-web", "flashcards", "ab", "123-shop" };

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            for (var index = 0; index < slugs.Length; index++)
            {
                // Explicit timestamps: `created_at default now()` is the transaction start, so the
                // order the backfill reads would otherwise rest on the id alone.
                var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, index, TimeSpan.Zero);
                await seed.ExecuteAsync(InsertDatedProjectSql, DatedProjectParameters(Guid.CreateVersion7(), first, slugs[index], slugs[index], createdAt));
                await seed.ExecuteAsync(InsertDatedProjectSql, DatedProjectParameters(Guid.CreateVersion7(), second, slugs[index], slugs[index], createdAt));
            }

            await seed.CommitAsync();
        }

        // The deploy path: every file before 0039 is journaled, so pointing the Migrator at the
        // repository's own directory applies 0039 — and, once a later migration lands, that one too,
        // which is why the assertion is about order and contiguity rather than an exact list.
        // A directory holding only 0039 would be refused as a gap by the runner's contiguous-run rule.
        var applied = _fixture.ApplyMigrations(database);
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        AssertDeployApplied(MigrationFile, applied);

        foreach (var org in new[] { first, second })
        {
            (await _fixture.ColumnAsync<string>(
                database,
                $"select case_prefix from public.project where org_id = '{org}' order by created_at, id"))
                .ShouldBe(["SHOP", "FLAS", "AB", "P123S"], $"org {org}: the oldest project keeps the bare stem");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.project where case_prefix is null or case_prefix !~ '{PrefixFormat}'"))
            .ShouldBe(0, "every prefix is non-NULL and inside the format check");

        // Per-org uniqueness, not global: `project_org_case_prefix_uq` is on (org_id, case_prefix).
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from (select org_id, case_prefix from public.project group by 1, 2 having count(*) > 1) duplicated"))
            .ShouldBe(0);

        (await _fixture.ConstraintsAsync(database, "project"))
            .ShouldContain(entry => entry.StartsWith("c:project_case_prefix_format:", StringComparison.Ordinal));
    }

    /// <summary>
    /// §4/AC4: the backfill dedupes against **every** prefix already assigned in the org, not against
    /// the stem's own partition. Fixtures seeded before 0039 in one org, expectation in
    /// <c>created_at, id</c> order: <c>sho</c> -> SHO, <c>sh-o</c> -> SHO2, <c>sho2</c> -> SHO22 (the
    /// second stem-SHO row holds SHO2, so `sho2` cannot keep its bare stem) and <c>123</c> -> P123,
    /// <c>1-23</c> -> P1232, <c>1232</c> -> P12322.
    /// <para>
    /// Two slugs of the plan's fixture list (`sho`, `sho` and `123`, `123`) cannot both exist in one
    /// org — <c>project_org_slug_key</c> is <c>unique (org_id, slug)</c> — so the duplicate stems are
    /// spelled with a hyphen (<c>sh-o</c>, <c>1-23</c>), which the 0012 slug grammar allows and which
    /// <c>evidgate.case_prefix_stem</c> strips to the same stem. The expectation values are exactly
    /// the plan's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CasePrefix_Backfill_DedupesAcrossStems()
    {
        await using var database = await BringToPreviousHeadAsync();
        var org = await SeedOrgAsync(database, "acme");

        var fixtures = new (string Slug, string Expected)[]
        {
            ("sho", "SHO"),
            ("sh-o", "SHO2"),
            ("sho2", "SHO22"),
            ("123", "P123"),
            ("1-23", "P1232"),
            ("1232", "P12322"),
        };

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            for (var index = 0; index < fixtures.Length; index++)
            {
                var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, index, TimeSpan.Zero);
                await seed.ExecuteAsync(
                    InsertDatedProjectSql,
                    DatedProjectParameters(Guid.CreateVersion7(), org, fixtures[index].Slug, fixtures[index].Slug, createdAt));
            }

            await seed.CommitAsync();
        }

        // Same deploy path as above: every file before 0039 is journaled, so the repository directory
        // applies 0039 (content_hash is written as the snapshot of the backfilled rows).
        var applied = _fixture.ApplyMigrations(database);
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        AssertDeployApplied(MigrationFile, applied);

        (await _fixture.ColumnAsync<string>(
            database,
            $"select slug || ':' || case_prefix from public.project where org_id = '{org}' order by created_at, id"))
            .ShouldBe(fixtures.Select(fixture => fixture.Slug + ":" + fixture.Expected).ToList());
    }

    // ------------------------------------------------------------------ case prefix: insert default (Q1)

    /// <summary>
    /// PANEL-CHOICE Q1: the BEFORE INSERT trigger fills a NULL prefix through the same stem+dedupe
    /// function as the backfill, so an INSERT with only <c>(id, org_id, name, slug)</c> — DevSeed and
    /// every existing test — keeps working and gets a well-formed, per-org unique value.
    /// </summary>
    [Fact]
    public async Task CasePrefix_InsertDefault_IsUniquePerOrg()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var acme = await SeedOrgAsync(database, "acme");
        var globex = await SeedOrgAsync(database, "globex");

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // Two projects of one org whose slugs share a stem, and one project of another org with
            // the same slug as the first — the prefix is unique per org, never globally.
            var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await seed.ExecuteAsync(
                InsertDatedProjectSql,
                DatedProjectParameters(Guid.CreateVersion7(), acme, "Checkout", "checkout", baseTime));
            await seed.ExecuteAsync(
                InsertDatedProjectSql,
                DatedProjectParameters(Guid.CreateVersion7(), acme, "Check out", "check-out", baseTime.AddMinutes(1)));
            await seed.ExecuteAsync(
                InsertDatedProjectSql,
                DatedProjectParameters(Guid.CreateVersion7(), globex, "Checkout", "checkout", baseTime));
            await seed.CommitAsync();
        }

        (await _fixture.ColumnAsync<string>(
            database,
            $"select slug || ':' || case_prefix from public.project where org_id = '{acme}' order by created_at, id"))
            .ShouldBe(["checkout:CHEC", "check-out:CHEC2"]);
        (await _fixture.ColumnAsync<string>(
            database,
            $"select case_prefix from public.project where org_id = '{globex}'"))
            .ShouldBe(["CHEC"], "another org may hold the same prefix");

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.project where case_prefix is null or case_prefix !~ '{PrefixFormat}'"))
            .ShouldBe(0);
    }

    /// <summary>
    /// The write model maps <c>case_prefix</c> store-generated (0039 / OI-13-101), so the project
    /// create path needs no change: EF omits the column, the BEFORE INSERT trigger fills it, and EF
    /// reads the value back through RETURNING inside the same tenant scope
    /// (<c>WriteModelMappingTests.ModelMatchesSchema</c> pins the mapping itself, this test pins the
    /// behaviour a wrong mapping would break — a required, non-generated shadow property would make
    /// every insert fail with a NULL violation).
    /// </summary>
    [Fact]
    public async Task CasePrefix_EfProjectInsert_ReadsTriggerValue()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, _) = await SeedTenantAsync(database, "acme", "checkout");

        await using var source = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var scopes = new NpgsqlTenantScopeFactory(source);
        await using var scope = await scopes.BeginAsync(org, cancellationToken: TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<TenancyWriteModelContext>()
            .UseNpgsql(scope.Connection)
            .AddInterceptors(new TenantCommandInterceptor(scopes))
            .UseTenantModel(typeof(OrganizationConfiguration).Assembly)
            .Options;

        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            OrgId = org,
            Name = "EF project",
            Slug = "ef-project",
            CreatedAt = at,
            UpdatedAt = at,
        };

        await using (var context = new TenancyWriteModelContext(options))
        {
            await context.Database.UseTransactionAsync(scope.Transaction, TestContext.Current.CancellationToken);
            await context.Set<Project>().AddAsync(project, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await scope.CommitAsync(TestContext.Current.CancellationToken);

        // "ef-project" -> "EFPR": the trigger filled the column EF omitted, and the seeded project in
        // this org already holds "CHEC", so nothing collides.
        (await _fixture.ColumnAsync<string>(
            database,
            $"select case_prefix from public.project where id = '{project.Id}'"))
            .ShouldBe(["EFPR"]);
    }

    /// <summary>
    /// An explicit prefix is kept byte-for-byte (the trigger only fills a NULL): the OI-13-101
    /// override a tenant admin will set is never recomputed from the slug, and the format check and
    /// the per-org unique index still refuse an invalid or a duplicate value.
    /// </summary>
    [Fact]
    public async Task CasePrefix_InsertExplicit_IsKeptVerbatim()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // The slug would stem to SHOP; the stored prefix must stay ZULU.
            await seed.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug, case_prefix) values (@id, @org_id, @name, @slug, 'ZULU')",
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("name", "Shop"),
                ("slug", "shop"));
            await seed.CommitAsync();
        }

        (await _fixture.ColumnAsync<string>(database, $"select case_prefix from public.project where org_id = '{org}'"))
            .ShouldBe(["ZULU"]);

        var duplicate = await CaptureOwnerErrorAsync(
            database,
            $"insert into public.project (id, org_id, name, slug, case_prefix) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', 'Other', 'other', 'ZULU')");
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("project_org_case_prefix_uq");

        var malformed = await CaptureOwnerErrorAsync(
            database,
            $"insert into public.project (id, org_id, name, slug, case_prefix) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', 'Lower', 'lower', 'zulu')");
        malformed.SqlState.ShouldBe("23514");
        malformed.ConstraintName.ShouldBe("project_case_prefix_format");
    }

    /// <summary>
    /// The <c>pg_advisory_xact_lock</c> half of Q1: two concurrent creates in one org with slugs that
    /// stem to the same value serialise on the per-org lock, so the second sees the first's committed
    /// prefix and takes the next candidate — no 23505 on <c>project_org_case_prefix_uq</c>.
    /// </summary>
    [Fact]
    public async Task CasePrefix_ConcurrentInserts_GetDistinctPrefixes()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");
        var connectionString = _fixture.OwnerConnectionString(database);
        var token = TestContext.Current.CancellationToken;

        await using var winner = new NpgsqlConnection(connectionString);
        await winner.OpenAsync(token);
        await using var winnerTransaction = await winner.BeginTransactionAsync(token);

        // The first create takes the org's advisory lock and holds it until commit.
        (await ExecuteAsync(winner, winnerTransaction, InsertProjectSql, ProjectParameters(Guid.CreateVersion7(), org, "Shop", "shop")))
            .ShouldBe(1);

        await using var loser = new NpgsqlConnection(connectionString);
        await loser.OpenAsync(token);
        await using var loserTransaction = await loser.BeginTransactionAsync(token);

        // `sh-op` stems to the same SHOP, so this create must wait for the lock instead of racing.
        var loserInsert = ExecuteAsync(loser, loserTransaction, InsertProjectSql, ProjectParameters(Guid.CreateVersion7(), org, "Sh op", "sh-op"));
        await Task.Delay(TimeSpan.FromMilliseconds(400), token);
        loserInsert.IsCompleted.ShouldBeFalse("the second create waits on the per-org advisory lock");

        await winnerTransaction.CommitAsync(token);
        await loserInsert.WaitAsync(TimeSpan.FromSeconds(30), token);
        await loserTransaction.CommitAsync(token);

        (await _fixture.ColumnAsync<string>(
            database,
            $"select slug || ':' || case_prefix from public.project where org_id = '{org}' order by created_at, id"))
            .ShouldBe(["shop:SHOP", "sh-op:SHOP2"]);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An INSERT of a project with only <c>(id, org_id, name, slug)</c>: the trigger fills the prefix.</summary>
    private const string InsertProjectSql =
        "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)";

    /// <summary>The same, with an explicit <c>created_at</c>/<c>updated_at</c> so the order is pinned.</summary>
    private const string InsertDatedProjectSql =
        "insert into public.project (id, org_id, name, slug, created_at, updated_at) "
        + "values (@id, @org_id, @name, @slug, @created_at, @created_at)";

    /// <summary>The parameters of <see cref="InsertProjectSql"/> — the 4-column INSERT.</summary>
    private static (string Name, object Value)[] ProjectParameters(Guid id, Guid org, string name, string slug) =>
    [
        ("id", id),
        ("org_id", org),
        ("name", name),
        ("slug", slug),
    ];

    /// <summary>The parameters of <see cref="InsertDatedProjectSql"/>, with the pinned timestamps.</summary>
    private static (string Name, object Value)[] DatedProjectParameters(
        Guid id,
        Guid org,
        string name,
        string slug,
        DateTimeOffset createdAt) =>
    [
        ("id", id),
        ("org_id", org),
        ("name", name),
        ("slug", slug),
        ("created_at", createdAt),
    ];

    /// <summary>An org and a project in it, written as the migration role (the owner policy permits it).</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = await SeedOrgAsync(database, orgSlug);
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(InsertProjectSql, ProjectParameters(project, org, projectSlug, projectSlug));
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>One organization row, written as the migration role.</summary>
    private async Task<Guid> SeedOrgAsync(string database, string slug)
    {
        var org = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", slug),
            ("slug", slug));
        await seed.CommitAsync();

        return org;
    }

    /// <summary>
    /// What a deploy run over the repository's own migration directory must satisfy, independent of
    /// how many files exist after <paramref name="migrationFile"/>: the file is applied, the applied
    /// set is contiguous (nothing at or before it is re-applied) and it is applied in lexical order.
    /// </summary>
    private static void AssertDeployApplied(string migrationFile, MigrationRun run)
    {
        run.Result.AppliedNow.ShouldContain(migrationFile);
        run.Result.AppliedNow
            .Where(name => string.CompareOrdinal(name, migrationFile) < 0)
            .ShouldBeEmpty($"no file at or before {migrationFile} may be re-applied; applied: {string.Join(", ", run.Result.AppliedNow)}");
        run.Result.AppliedNow.Order(StringComparer.Ordinal).ToArray()
            .ShouldBe(run.Result.AppliedNow.ToArray(), "the pending files are applied in lexical order");
    }

    /// <summary>A database brought to the head **before** 0039, so the backfill sees pre-existing rows.</summary>
    private async Task<DedicatedDatabase> BringToPreviousHeadAsync()
    {
        var database = await _fixture.CreateEmptyDedicatedDatabaseAsync();

        // Every migration file lexically before the one under test: the real expand path, and it
        // keeps working when later migrations are added to the repository.
        var prefixes = Directory.EnumerateFiles(PostgresTenancyFixture.RepoMigrationsPath, "*.sql")
            .Select(Path.GetFileName)
            .Where(name => string.CompareOrdinal(name, MigrationFile) < 0)
            .Select(name => name![..4])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var history = TempMigrationDirectory.CreateFrom(PostgresTenancyFixture.RepoMigrationsPath, prefixes);
        var baselined = _fixture.ApplyMigrations(database, history.Path);
        baselined.ExitCode.ShouldBe(MigratorExitCodes.Success, baselined.Transcript);
        baselined.Result.AppliedNow.ShouldNotContain(MigrationFile);

        return database;
    }

    /// <summary>
    /// An index definition with the double quotes removed: <c>pg_get_indexdef</c> quotes reserved
    /// words such as <c>"position"</c>, and the assertions below compare columns, not quoting style.
    /// </summary>
    private static string Unquoted(string indexDefinition) =>
        indexDefinition.Replace("\"", string.Empty, StringComparison.Ordinal);

    /// <summary>An INSERT of a feature node; the caller supplies the position and the parent.</summary>
    private static string InsertNode(Guid id, Guid org, Guid project, Guid? parent, string kind, string title, int position) =>
        "insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position) "
        + $"values ('{id}', '{org}', '{project}', {(parent is null ? "null" : $"'{parent}'")}, '{kind}', '{title}', {position.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Runs one statement as the migration role inside a fresh transaction and returns the server error.</summary>
    private async Task<PostgresException> CaptureOwnerErrorAsync(string database, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>Runs one statement on an open connection and transaction.</summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
