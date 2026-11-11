// 0098's column on a real PostgreSQL: the CHECK admits only the repo's `sha256:<lowercase hex>` form or NULL, and the
// append-only ACL keeps the app role from rewriting the hash of a row it wrote. The store's own writes are asserted in
// the ledger suite; this file is the schema contract those writes rely on.
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 0098's <c>usage_ledger_entry.payload_hash</c>: a nullable <c>text</c> with the repo's digest CHECK (0021/0022 shape).
/// No new grant is needed for it — <c>evidgate_app</c> holds INSERT and SELECT on the table only (0046), so the hash is
/// immutable by ACL exactly like every other column — and that is asserted, not assumed.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageLedgerPayloadHashSchemaTests
{
    private const string Constraint = "usage_ledger_entry_payload_hash_check";

    private const string ValidHash =
        "sha256:e9b14db58364fb693b62b1bac8c0aaa38006ca363b805095b12539ac31674e1a";

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerPayloadHashSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Column_IsNullableText_WithTheDigestCheck()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var columns = await _fixture.ColumnsAsync(database, "usage_ledger_entry");
        columns.ShouldContain(
            column => column.StartsWith("payload_hash:text:text:YES:", StringComparison.Ordinal),
            "the hash is a nullable text column: a legacy row has none");

        (await _fixture.ConstraintNamesAsync(database, "usage_ledger_entry"))
            .ShouldContain(name => name.EndsWith($":{Constraint}", StringComparison.Ordinal));

        var definition = await _fixture.ScalarAsync<string>(
            database,
            $"select pg_catalog.pg_get_constraintdef(oid) from pg_catalog.pg_constraint where conname = '{Constraint}'");
        definition.ShouldContain("sha256:");
        definition.ShouldNotContain("not valid", Case.Insensitive);
    }

    /// <summary>
    /// A NULL is the legacy row and a canonical digest is a written row; anything else is refused under the named CHECK,
    /// so a writer cannot store a hash that could never be compared.
    /// </summary>
    /// <param name="value">The value the CHECK must refuse.</param>
    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("sha256:e9b14db58364fb693b62b1bac8c0aaa38006ca363b805095b12539ac31674e1")]
    [InlineData("SHA256:E9B14DB58364FB693B62B1BAC8C0AAA38006CA363B805095B12539AC31674E1A")]
    [InlineData("e9b14db58364fb693b62b1bac8c0aaa38006ca363b805095b12539ac31674e1a")]
    [InlineData("md5:e9b14db58364fb693b62b1bac8c0aaa38006ca363b805095b12539ac31674e1a")]
    public async Task Check_RefusesAnythingButTheCanonicalForm(string value)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org);

        var error = await owner.CaptureErrorAsync(
            LedgerInsertSql(hashParameter: true),
            LedgerParameters(org, project, Guid.CreateVersion7(), Unique.Slug("hash-check"), value));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe(Constraint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ValidHash)]
    public async Task Check_AcceptsNullAndTheCanonicalForm(string? value)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org);

        var parameters = LedgerParameters(
            org, project, Guid.CreateVersion7(), Unique.Slug("hash-check"), value);

        (await owner.ExecuteAsync(LedgerInsertSql(hashParameter: value is not null), parameters)).ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>
    /// The row is append-only, and the hash is one more column the app role cannot rewrite: 0046 grants SELECT and
    /// INSERT only, so both an UPDATE of the hash and a DELETE of the row are permission errors.
    /// </summary>
    [Fact]
    public async Task AppRole_CannotRewriteOrDeleteTheHash()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database);
        var id = Guid.CreateVersion7();
        var billingKey = Unique.Slug("hash-acl");

        var parameters = LedgerParameters(org, project, id, billingKey, ValidHash);

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await app.ExecuteAsync(LedgerInsertSql(hashParameter: true), parameters))
                .ShouldBe(1, "evidgate_app holds INSERT on the ledger");
            await app.CommitAsync();
        }

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var update = await app.CaptureErrorAsync(
                $"update public.usage_ledger_entry set payload_hash = 'sha256:{new string('b', 64)}' where id = '{id}'");
            update.SqlState.ShouldBe("42501", "the hash is immutable by ACL");
        }

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var delete = await app.CaptureErrorAsync($"delete from public.usage_ledger_entry where id = '{id}'");
            delete.SqlState.ShouldBe("42501", "the ledger row itself is append-only");
        }

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_ledger_entry", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_ledger_entry", "DELETE")).ShouldBeFalse();
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where id = '{id}' and payload_hash = '{ValidHash}'"))
            .ShouldBe(1, "a refused UPDATE/DELETE leaves the row byte-identical");
    }

    private static string LedgerInsertSql(bool hashParameter) =>
        "insert into public.usage_ledger_entry "
        + "(id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at"
        + (hashParameter ? ", payload_hash" : string.Empty)
        + ") values (@id, @org_id, @project_id, 'results', 1.5, 'batch', '2026-10', @billing_key, @occurred_at"
        + (hashParameter ? ", @payload_hash" : string.Empty)
        + ")";

    private static (string Name, object Value)[] LedgerParameters(
        Guid org,
        Guid project,
        Guid id,
        string billingKey,
        string? hash)
    {
        var parameters = new List<(string Name, object Value)>
        {
            ("id", id),
            ("org_id", org),
            ("project_id", project),
            ("billing_key", billingKey),
            ("occurred_at", DateTimeOffset.UtcNow),
        };

        if (hash is not null)
        {
            parameters.Add(("payload_hash", hash));
        }

        return [.. parameters];
    }

    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", "payload-hash"),
            ("slug", Unique.Slug("hash-org")));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", "checkout"),
            ("slug", Unique.Slug("hash-project")));
        await seed.CommitAsync();

        return (org, project);
    }
}
