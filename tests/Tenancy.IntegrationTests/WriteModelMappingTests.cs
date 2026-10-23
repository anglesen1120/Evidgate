using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.1.1-T1.3: the EF Core write model of the <c>Tenancy</c> module builds over
/// the Npgsql provider through the production path — a context derived from
/// <see cref="EvidgateDbContext"/> whose options carry <c>UseTenantModel</c>
/// (01.4.2-T1.3), so the authoritative <c>TenantModelConventions</c> run last —
/// and maps to exactly the schema migrations 0004/0005 create: same table, same
/// columns, same key, index and foreign-key names. A model that drifts from the
/// SQL is the failure mode this test exists for.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class WriteModelMappingTests
{
    /// <summary>The snake_case contract of 00 §7: lower-case words joined by single underscores.</summary>
    private const string SnakeCase = "^[a-z0-9]+(_[a-z0-9]+)*$";

    private readonly PostgresTenancyFixture _fixture;

    public WriteModelMappingTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ModelMatchesSchema()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var context = TenancyWriteModelContext.Create();

        var organization = context.Model.FindEntityType(typeof(Organization)).ShouldNotBeNull();
        var project = context.Model.FindEntityType(typeof(Project)).ShouldNotBeNull();

        await AssertColumnSetAsync(organization, database, "organization");
        await AssertColumnSetAsync(project, database, "project");

        // Key, index and FK names are the SQL constraint names, so the model and
        // the migration cannot drift apart silently.
        organization.GetKeys().ShouldHaveSingleItem().GetName().ShouldBe("organization_pkey");
        project.GetKeys().Where(key => key.IsPrimaryKey()).ShouldHaveSingleItem().GetName().ShouldBe("project_pkey");

        // (org_id, id) is the composite tenant key every project-scoped table
        // references (00 §5.4.6, TL T3).
        project.GetKeys()
            .Where(key => !key.IsPrimaryKey())
            .ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(
                key => key.GetName().ShouldBe("project_org_id_key"),
                key => key.Properties.Select(property => property.GetColumnName())
                    .ShouldBe(["org_id", "id"]));

        // The FK to organization, and the covering index of the keyset list query (00 §5.7 P4).
        project.GetForeignKeys().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            foreignKey => foreignKey.GetConstraintName().ShouldBe("project_organization_fk"),
            foreignKey => foreignKey.PrincipalEntityType.ClrType.ShouldBe(typeof(Organization)),
            foreignKey => foreignKey.Properties.ShouldHaveSingleItem().GetColumnName().ShouldBe("org_id"));

        var indexColumns = new[] { "org_id", "status", "created_at", "id" };
        var slugKeyColumns = new[] { "org_id", "slug" };
        project.GetIndexes()
            .Select(index => new
            {
                Name = index.GetDatabaseName(),
                index.IsUnique,
                Columns = index.Properties.Select(property => property.GetColumnName()).ToList(),
            })
            .ShouldSatisfyAllConditions(
                indexes => indexes.ShouldContain(index => index.Name == "project_org_status_created_id_idx"
                    && !index.IsUnique
                    && index.Columns.SequenceEqual(indexColumns)),
                // `unique (org_id, slug)` of migration 0005.
                indexes => indexes.ShouldContain(index => index.Name == "project_org_slug_key"
                    && index.IsUnique
                    && index.Columns.SequenceEqual(slugKeyColumns)));

        // The context must be the production one — EvidgateDbContext, whose options carry
        // UseTenantModel — so the model below is the model the host builds (01.4.2-T1.3).
        // A bare DbContext applying only the module configurations would pass the name,
        // key and index assertions above and still lose every convention.
        context.ShouldBeAssignableTo<EvidgateDbContext>();
        context.GetService<IDbContextOptions>().Extensions.ShouldContain(
            extension => extension.Info.LogFragment.StartsWith("tenantModel=", StringComparison.Ordinal));

        // What the module configurations do NOT express and only TenantModelConventions
        // supplies (00 §7): `version` is store-generated on update, because the migration
        // trigger set_updated_at_and_version — 0004 for `organization`, 0005 for `project`,
        // M1W3-01 — bumps it, and `id` carries a client-side UUIDv7 generator so a
        // caller that forgets still gets a v7 id and EF never substitutes a v4 Guid
        // or a zero one. Both are asserted on the production model, not on the configurations.
        foreach (var entityType in new[] { organization, project })
        {
            entityType.GetProperties()
                .Single(property => property.GetColumnName() == "version")
                .ShouldSatisfyAllConditions(
                    property => property.IsConcurrencyToken.ShouldBeTrue(),
                    property => property.ValueGenerated.ShouldBe(ValueGenerated.OnUpdate));

            entityType.GetProperties()
                .Single(property => property.GetColumnName() == "id")
                .ShouldSatisfyAllConditions(
                    property => property.ClrType.ShouldBe(typeof(Guid)),
                    property => property.ValueGenerated.ShouldBe(ValueGenerated.OnAdd),
                    property => property.GetValueGeneratorFactory().ShouldNotBeNull());
        }

        // The conventions are authoritative and run last over the whole model, so every
        // mapped table and column of the production model has to be snake_case — not just
        // the two entities asserted above (00 §7). Key and index segments are the same
        // IProperty instances GetProperties() returns, so they are covered here too.
        context.Model.GetEntityTypes().ShouldNotBeEmpty();
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            entityType.GetTableName().ShouldNotBeNull().ShouldMatch(SnakeCase);
            entityType.GetSchema().ShouldBe("public");

            foreach (var property in entityType.GetProperties())
            {
                property.GetColumnName().ShouldNotBeNull().ShouldMatch(SnakeCase);
            }
        }
    }

    /// <summary>The mapped column names must equal the physical columns of the migrated table.</summary>
    private async Task AssertColumnSetAsync(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string database,
        string table)
    {
        entityType.GetTableName().ShouldBe(table);
        entityType.GetSchema().ShouldBe("public");

        entityType.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(
                (await _fixture.ColumnsAsync(database, table))
                    .Select(column => column.Split(':')[0])
                    .Order(StringComparer.Ordinal));
    }
}

/// <summary>
/// The module context the production path builds (01.4.2-T1.3): it derives from
/// <see cref="EvidgateDbContext"/> and its options carry <c>UseTenantModel</c> with
/// the module assembly, so the module's <c>IEntityTypeConfiguration</c> classes
/// and the authoritative <c>TenantModelConventions</c> are exactly the ones the
/// host applies. The connection string is never opened: building a model is
/// offline. The <c>TenantCommandInterceptor</c> is not added — building a model
/// executes nothing, and the AC1 guard has its own tests in
/// <c>Persistence.IntegrationTests</c>.
/// </summary>
internal sealed class TenancyWriteModelContext(DbContextOptions<TenancyWriteModelContext> options)
    : EvidgateDbContext(options)
{
    internal static TenancyWriteModelContext Create() =>
        new(new DbContextOptionsBuilder<TenancyWriteModelContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(OrganizationConfiguration).Assembly)
            .Options);
}
