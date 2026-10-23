using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.IntegrationTests;

/// <summary>
/// The shape a real module ships (00 §5.5 C4): a sealed context over
/// <see cref="EvidgateDbContext"/> that says nothing about tenancy, plus its own
/// <see cref="IEntityTypeConfiguration{TEntity}"/> class. It maps the fixture
/// table <c>public.rls_probe</c> of migration 0003 by class name alone — the
/// snake_case table name is the convention's job (01.4.2-T1.3).
/// </summary>
public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : EvidgateDbContext(options);

/// <summary>One business row of the tenancy fixture table.</summary>
public sealed class RlsProbe
{
    public Guid OrgId { get; set; }

    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>Maps <see cref="RlsProbe"/> onto <c>public.rls_probe</c> (composite tenant key, as every business table).</summary>
public sealed class RlsProbeConfiguration : IEntityTypeConfiguration<RlsProbe>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RlsProbe> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasKey(probe => new { probe.OrgId, probe.Id });
        builder.Property(probe => probe.Label).IsRequired();
    }
}
