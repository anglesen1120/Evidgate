using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The issued billing statement against a real PostgreSQL: the migration, its tenant key, the period
/// key that makes an issue idempotent, the tenant-root reference, the bounded document checks, forced
/// row level security with the app-scoped policy, and the append-only grant that refuses an edit of a
/// document already issued.
/// <para>
/// The fixture is the suite's own <see cref="PostgresTenancyFixture"/>, so the table under test is the
/// one the real migrator applied. Every write runs as <c>evidgate_app</c> inside a tenant-scoped
/// transaction unless a case deliberately speaks as the owner (the migration/backfill role), the
/// operator, or with no tenant set.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class UsageBillingStatementSchemaTests
{
    private const string Table = "billing_statement";
    private const string MigrationFile = "0080_usage_billing_statement.sql";
    private const string Period = "2026-09";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly PostgresTenancyFixture _fixture;

    public UsageBillingStatementSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) the migration and the declared shape

    [Fact]
    public async Task Migration_IsJournalled_AndDeclaresTheStatementContract()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue(
            "the real migrator must have created billing_statement");

        var journal = await _fixture.ReadJournalAsync(database);
        journal.ShouldContain(row => row.Name == MigrationFile, "0080 must be journalled by the real runner");
        journal.ShouldAllBe(row => row.Checksum.Length == 64);

        var columns = await _fixture.ColumnsAsync(database, Table);
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:NO:",
            "period:text:text:NO:",
            "kind:text:text:NO:",
            "total_amount:numeric:numeric:NO:",
            "currency:text:text:NO:",
            "line_count:integer:int4:NO:",
            "content_hash:text:text:NO:",
            "worksheet_json:jsonb:jsonb:NO:",
            "issued_by:uuid:uuid:NO:",
            "issued_at:timestamp with time zone:timestamptz:NO:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            columns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"billing_statement needs the declared column shape '{prefix}'");
        }

        var constraints = await _fixture.ConstraintsAsync(database, Table);
        constraints.ShouldContain("p:billing_statement_pkey:PRIMARY KEY (id)");
        constraints.ShouldContain("u:billing_statement_org_id_key:UNIQUE (org_id, id)");
        constraints.ShouldContain(
            "u:billing_statement_period_kind_key:UNIQUE (org_id, period, kind)",
            "the period key is what makes a re-issue collide instead of writing a second statement");

        var names = await _fixture.ConstraintNamesAsync(database, Table);
        names.ShouldContain("f:billing_statement_organization_fk");
        foreach (var check in new[]
        {
            "c:billing_statement_period_check",
            "c:billing_statement_kind_check",
            "c:billing_statement_currency_check",
            "c:billing_statement_line_count_check",
            "c:billing_statement_content_hash_check",
            "c:billing_statement_worksheet_size_check",
            "c:billing_statement_version_check",
        })
        {
            names.ShouldContain(check, $"billing_statement must declare {check}");
        }

        // The tenant-root reference is the org column alone: organization carries no org_id of its own.
        constraints.ShouldContain(constraint => constraint.StartsWith("f:billing_statement_organization_fk:", StringComparison.Ordinal)
            && constraint.Contains("FOREIGN KEY (org_id) REFERENCES organization(id)", StringComparison.Ordinal));

        // The shared concurrency trigger is attached; the issued-period guard refuses every UPDATE before it fires.
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_trigger where tgrelid = 'public.billing_statement'::regclass"
            + " and tgname = 'billing_statement_set_updated_at_and_version' and not tgisinternal"))
            .ShouldBe(1, "billing_statement must attach the shared version trigger");

        (await _fixture.RowSecurityAsync(database, Table)).ShouldBe(
            (true, true), "row level security must be enabled and must also apply to the owner");

        // Only the two named-role policies exist: a policy without `to` would reach every role.
        (await _fixture.PoliciesAsync(database, Table)).ShouldBe(
        [
            "billing_statement_owner_backfill:evidgate_owner:*:true:true",
            "billing_statement_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
    }

    // ------------------------------------------------------------------ (b) append-only by grant

    [Fact]
    public async Task Statement_UpdateDelete_Rejected()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");
        var id = Guid.CreateVersion7();

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var row = StatementInsert(id, org);
            (await app.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "evidgate_app holds INSERT on a statement");
            await app.CommitAsync();
        }

        // A statement is a document: the app role can neither edit nor drop it.
        var update = await CaptureErrorAsync(
            _fixture.AppConnectionString(database),
            org,
            $"update public.billing_statement set total_amount = 99 where id = '{id}'");
        update.SqlState.ShouldBe("42501", "UPDATE on an issued statement must be a permission error");

        var delete = await CaptureErrorAsync(
            _fixture.AppConnectionString(database),
            org,
            $"delete from public.billing_statement where id = '{id}'");
        delete.SqlState.ShouldBe("42501", "DELETE on an issued statement must be a permission error");

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.billing_statement where id = '{id}' and total_amount = 10.50 and version = 1"))
            .ShouldBe(1, "the refused statements must leave the document unchanged");

        foreach (var (role, privilege, expected) in new[]
        {
            ("evidgate_app", "SELECT", true),
            ("evidgate_app", "INSERT", true),
            ("evidgate_app", "UPDATE", false),
            ("evidgate_app", "DELETE", false),
            ("evidgate_app", "TRUNCATE", false),
            ("evidgate_operator", "SELECT", true),
            ("evidgate_worker_sched", "SELECT", false),
        })
        {
            (await _fixture.HasTablePrivilegeAsync(database, role, Table, privilege))
                .ShouldBe(expected, $"{role} {privilege} on {Table}");
        }

        // The owner is refused too (the issued-period guard binds every role), and the refusal changes nothing.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org))
        {
            (await owner.CaptureErrorAsync($"update public.billing_statement set total_amount = 11 where id = '{id}'"))
                .SqlState.ShouldBe("23514");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.billing_statement where id = '{id}' and total_amount = 10.50 and version = 1"))
            .ShouldBe(1, "the owner's refused update leaves the document unchanged");
    }

    // ------------------------------------------------------------------ (c) one statement per period

    [Fact]
    public async Task Statement_DuplicatePeriodAndKind_RaisesUniqueViolation()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");
        var period = "2026-08";

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var first = StatementInsert(Guid.CreateVersion7(), org, period: period);
        (await app.ExecuteAsync(first.Sql, first.Parameters)).ShouldBe(1);

        var second = StatementInsert(Guid.CreateVersion7(), org, period: period, contentHash: new string('b', 64));
        var duplicate = await app.CaptureErrorAsync(second.Sql, second.Parameters);
        duplicate.SqlState.ShouldBe("23505", "a second statement of one period must collide on its key");
        duplicate.ConstraintName.ShouldBe("billing_statement_period_kind_key");
    }

    // ------------------------------------------------------------------ (d) the bounds the document assumes

    [Fact]
    public async Task Statement_CheckBoundaries_RejectBadPeriodKindCurrencyAndDocument()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");

        // Positive control: the same shape with only the variable under test changed is accepted.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var valid = StatementInsert(Guid.CreateVersion7(), org, period: "2026-07");
            (await app.ExecuteAsync(valid.Sql, valid.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        // Every case owns a period of its own: a check that failed to fire would otherwise collide on the
        // period key of an earlier case and report a uniqueness violation instead of the missing check.
        foreach (var (insert, expectedConstraint) in new (Func<Guid, (string Sql, (string Name, object Value)[] Parameters)>, string)[]
        {
            (id => StatementInsert(id, org, period: "2026-13"), "billing_statement_period_check"),
            (id => StatementInsert(id, org, period: "2026-7"), "billing_statement_period_check"),
            (id => StatementInsert(id, org, period: "not-a-period"), "billing_statement_period_check"),
            (id => StatementInsert(id, org, period: "2026-01", kind: "credit"), "billing_statement_kind_check"),
            (id => StatementInsert(id, org, period: "2026-02", currency: "usd"), "billing_statement_currency_check"),
            (id => StatementInsert(id, org, period: "2026-03", currency: "US"), "billing_statement_currency_check"),
            (id => StatementInsert(id, org, period: "2026-04", lineCount: -1), "billing_statement_line_count_check"),
            (id => StatementInsert(id, org, period: "2026-05", contentHash: "abc"), "billing_statement_content_hash_check"),
            (id => StatementInsert(id, org, period: "2026-10", contentHash: new string('A', 64)), "billing_statement_content_hash_check"),
            (id => StatementInsert(id, org, period: "2026-11", worksheetJson: "[]"), "billing_statement_worksheet_size_check"),
        })
        {
            // Each refused statement aborts its own transaction, so each gets its own scope.
            var refused = insert(Guid.CreateVersion7());
            var error = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, refused.Sql, refused.Parameters);
            error.SqlState.ShouldBe("23514", expectedConstraint);
            error.ConstraintName.ShouldBe(expectedConstraint);
        }
    }

    // ------------------------------------------------------------------ (e) tenant isolation

    [Fact]
    public async Task Statement_CrossTenant_IsInvisibleAndForeignInsertRefused()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");
        var other = await SeedOrgAsync(database, "beta");
        var period = "2026-06";

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var row = StatementInsert(Guid.CreateVersion7(), org, period: period);
            (await app.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        await using (var foreign = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), other))
        {
            (await foreign.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(other);
            (await foreign.ScalarAsync<long>($"select count(*) from public.{Table}")).ShouldBe(
                0, "another tenant must not read an issued statement");

            // The payload names the other org, so the policy's with-check half refuses the write.
            var denied = StatementInsert(Guid.CreateVersion7(), org, period: "2026-05");
            var error = await foreign.CaptureErrorAsync(denied.Sql, denied.Parameters);
            error.SqlState.ShouldBe("42501", "a cross-tenant statement INSERT must fail the with-check");
        }

        // No tenant setting: the helpers answer NULL, so the policy matches nothing.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await unset.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await unset.ScalarAsync<long>($"select count(*) from public.{Table}")).ShouldBe(0);
        }

        // The operator's SELECT grant stays inert without an operator policy.
        await using (var op = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database), org))
        {
            (await op.ScalarAsync<long>($"select count(*) from public.{Table}")).ShouldBe(0);
        }

        // The row still exists for its own tenant, so the zeros above are the policy and not an empty table.
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.{Table} where org_id = '{org}' and period = '{period}'"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One statement INSERT; the caller appends nothing, so a NULL never needs a typed parameter.</summary>
    private static (string Sql, (string Name, object Value)[] Parameters) StatementInsert(
        Guid id,
        Guid org,
        string period = Period,
        string kind = "invoice",
        decimal totalAmount = 10.50m,
        string currency = "USD",
        int lineCount = 2,
        string? contentHash = null,
        string worksheetJson = "{\"lines\":[]}",
        Guid? issuedBy = null)
    {
        const string sql =
            """
            insert into public.billing_statement
                (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
                 issued_by, issued_at)
            values
                (@id, @org_id, @period, @kind, @total_amount, @currency, @line_count, @content_hash,
                 @worksheet_json::jsonb, @issued_by, @issued_at)
            """;

        return (sql,
        [
            ("id", id),
            ("org_id", org),
            ("period", period),
            ("kind", kind),
            ("total_amount", totalAmount),
            ("currency", currency),
            ("line_count", lineCount),
            ("content_hash", contentHash ?? Hash),
            ("worksheet_json", worksheetJson),
            ("issued_by", issuedBy ?? Guid.CreateVersion7()),
            ("issued_at", DateTimeOffset.UtcNow),
        ]);
    }

    /// <summary>One organisation, written as the migration role; the statement's only parent.</summary>
    private async Task<Guid> SeedOrgAsync(string database, string slug)
    {
        var org = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", slug),
            ("slug", Unique.Slug(slug)));
        await seed.CommitAsync();

        return org;
    }

    /// <summary>Runs one statement in a fresh transaction for <paramref name="connectionString"/> and returns the server error.</summary>
    private static async Task<PostgresException> CaptureErrorAsync(
        string connectionString,
        Guid org,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        return await scope.CaptureErrorAsync(sql, parameters);
    }
}
