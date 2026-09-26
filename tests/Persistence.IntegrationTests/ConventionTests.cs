using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// 01.4.2-T3: the column conventions of <c>TenantModelConventions</c> (00 §7,
/// §5.4.4, §5.5 C4) as the production path builds the model — snake_case table and
/// column names, an application-generated UUIDv7 <c>id</c>, the <c>version</c>
/// optimistic-concurrency token the database increments, <c>created_at</c>/
/// <c>updated_at</c> as <c>timestamptz</c>, and the fixed <c>duration_ms</c> name
/// for durations. It also pins the fix-1 contract: an explicitly configured table
/// or column name that agrees with the convention is accepted, one that disagrees
/// fails the model build with the entity and the property named — instead of being
/// silently overwritten. No database is opened: building a model is offline, so
/// this class carries no Docker trait.
/// </summary>
public sealed class ConventionTests
{
    /// <summary>
    /// The model the production path builds carries the 00 §7 conventions, and the
    /// sample's explicitly configured names — the snake_case spelling of the very
    /// same names — are accepted, not rejected.
    /// </summary>
    [Fact]
    public void Model_Builds_The_00_7_Column_Conventions()
    {
        using var context = ConventionModelContext.Create();

        var sample = context.Model.FindEntityType(typeof(ConventionSample)).ShouldNotBeNull();

        // The table is the snake_case, singular CLR type name (explicitly configured
        // as `convention_sample` here, which is why this also proves an agreeing
        // explicit name is accepted).
        sample.GetTableName().ShouldBe("convention_sample");

        foreach (var (property, column) in new[]
                 {
                     (nameof(ConventionSample.OrgId), "org_id"),
                     (nameof(ConventionSample.CreatedAt), "created_at"),
                     (nameof(ConventionSample.UpdatedAt), "updated_at"),
                     (nameof(ConventionSample.Version), "version"),
                     (nameof(ConventionSample.DurationMs), "duration_ms"),
                 })
        {
            sample.FindProperty(property).ShouldNotBeNull().GetColumnName().ShouldBe(column);
        }

        // 00 §7/§5.4.4: times are UTC `timestamptz`, whatever the CLR spelling. The
        // mapping is the Npgsql provider's (a store type is deliberately not pinned,
        // so an explicit one is never silently overridden); this pins it at the model
        // level so a provider change is a failure here, and SchemaConventionTests'
        // audit-column rule pins the migrated column.
        sample.FindProperty(nameof(ConventionSample.CreatedAt))!.GetColumnType().ShouldBe("timestamp with time zone");
        sample.FindProperty(nameof(ConventionSample.UpdatedAt))!.GetColumnType().ShouldBe("timestamp with time zone");

        // 00 §7: `version` is the concurrency token, and the database increments it
        // through the `set_updated_at_and_version` trigger (0004/0005), so EF reads
        // the new value back instead of writing one.
        var version = sample.FindProperty(nameof(ConventionSample.Version)).ShouldNotBeNull();
        version.IsConcurrencyToken.ShouldBeTrue();
        version.ValueGenerated.ShouldBe(ValueGenerated.OnUpdate);

        // 00 §7: `id` is the application's UUIDv7 — OnAdd with a client-side
        // generator, so a caller's explicit id wins and a forgotten one still gets
        // a v7 value; EF never substitutes a store/v4 Guid.
        var id = sample.FindProperty(nameof(ConventionSample.Id)).ShouldNotBeNull();
        id.ClrType.ShouldBe(typeof(Guid));
        id.ValueGenerated.ShouldBe(ValueGenerated.OnAdd);
        id.GetValueGeneratorFactory().ShouldNotBeNull();
    }

    /// <summary>
    /// 00 §7/T3, and the failure the model used to allow: a caller that leaves the id
    /// unset must still get a UUIDv7. Until the client-side generator existed, `Never`
    /// made EF insert the zero Guid verbatim — the first forgotten id silently
    /// became `00000000-0000-0000-0000-000000000000` and the second one threw. So the
    /// generator is asserted by its effect, not only by the metadata: the value is
    /// generated at `Add`, it is a v7, it is not a temporary key (no store read-back),
    /// and an id the caller did set is left alone.
    /// </summary>
    [Fact]
    public void Unset_Id_Is_Generated_As_Uuidv7()
    {
        using var context = ConventionModelContext.Create();

        var generated = context.Add(new ConventionSample());
        var id = generated.Property(sample => sample.Id);

        id.IsTemporary.ShouldBeFalse();
        id.CurrentValue.ShouldNotBe(Guid.Empty);
        id.CurrentValue.Version.ShouldBe(7);

        var explicitId = Guid.CreateVersion7();
        context.Add(new ConventionSample { Id = explicitId })
            .Property(sample => sample.Id)
            .CurrentValue.ShouldBe(explicitId);
    }

    /// <summary>
    /// Fix 1: an explicitly configured table name outside the convention fails the
    /// model build and names the entity, the offending name and the expected one —
    /// the silent overwrite it replaces would leave the module configured against a
    /// table that does not exist (00 §5.5 C4).
    /// </summary>
    [Fact]
    public void Explicit_Table_Name_Outside_The_Convention_Is_Rejected()
    {
        using var context = WrongTableNameContext.Create();

        var error = Should.Throw<InvalidOperationException>(() => _ = context.Model);

        error.Message.ShouldContain("ConventionSample");
        error.Message.ShouldContain("WrongTableName");
        error.Message.ShouldContain("convention_sample");
        error.Message.ShouldContain("00 §7");
    }

    /// <summary>Fix 1, column half: the same for a column, naming entity and property.</summary>
    [Fact]
    public void Explicit_Column_Name_Outside_The_Convention_Is_Rejected()
    {
        using var context = WrongColumnNameContext.Create();

        var error = Should.Throw<InvalidOperationException>(() => _ = context.Model);

        error.Message.ShouldContain("ConventionSample.OrgId");
        error.Message.ShouldContain("OrgID");
        error.Message.ShouldContain("org_id");
        error.Message.ShouldContain("00 §7");
    }

    /// <summary>
    /// The attribute path (<c>ConfigurationSource.DataAnnotation</c>): <c>[Table]</c>
    /// and <c>[Column]</c> are as explicit as the fluent API, so names equal to the
    /// convention are accepted here too.
    /// </summary>
    [Fact]
    public void Attribute_Names_Equal_To_The_Convention_Are_Accepted()
    {
        using var context = AttributeNameContext.Create();

        var sample = context.Model.FindEntityType(typeof(AttributeSample)).ShouldNotBeNull();

        sample.GetTableName().ShouldBe("attribute_sample");
        sample.FindProperty(nameof(AttributeSample.OrgId)).ShouldNotBeNull().GetColumnName().ShouldBe("org_id");
    }

    /// <summary>And a <c>[Table]</c> name outside the convention fails the build, exactly as the fluent API does.</summary>
    [Fact]
    public void Attribute_Table_Name_Outside_The_Convention_Is_Rejected()
    {
        using var context = WrongAttributeNameContext.Create();

        var error = Should.Throw<InvalidOperationException>(() => _ = context.Model);

        error.Message.ShouldContain("WrongAttributeSample");
        error.Message.ShouldContain("WrongAttributeTable");
        error.Message.ShouldContain("wrong_attribute_sample");
    }

    /// <summary>
    /// The <c>[Column]</c> half of the attribute path: it is checked through
    /// <c>GetColumnNameConfigurationSource()</c>, a different EF extension method
    /// than the table one, so it gets its own case.
    /// </summary>
    [Fact]
    public void Attribute_Column_Name_Outside_The_Convention_Is_Rejected()
    {
        using var context = WrongAttributeColumnContext.Create();

        var error = Should.Throw<InvalidOperationException>(() => _ = context.Model);

        error.Message.ShouldContain("WrongAttributeColumnSample.OrgId");
        error.Message.ShouldContain("OrgID");
        error.Message.ShouldContain("org_id");
    }

    /// <summary>
    /// Fix 1 must not fire on a name EF's own conventions set: a context that
    /// declares <c>DbSet&lt;ConventionSample&gt; Samples</c> makes
    /// <c>TableNameFromDbSetConvention</c> map the table to "Samples" at convention
    /// priority when the entity type is added. Nothing is explicitly configured, so
    /// the model has to build and the table has to stay <c>convention_sample</c> —
    /// every module context of 02–08 declares DbSets.
    /// </summary>
    [Fact]
    public void DbSet_Name_Does_Not_Fail_The_Convention()
    {
        using var context = DbSetNameContext.Create();

        var sample = context.Model.FindEntityType(typeof(ConventionSample)).ShouldNotBeNull();

        sample.GetTableName().ShouldBe("convention_sample");
        sample.FindProperty(nameof(ConventionSample.OrgId)).ShouldNotBeNull().GetColumnName().ShouldBe("org_id");
    }

    /// <summary>
    /// QA-A-11: every type of a TPT (and TPC) hierarchy owns a table, so the
    /// convention has to name those tables too — a derived type must not keep its
    /// CLR name. A TPH derived type still shares its root's table.
    /// </summary>
    [Fact]
    public void Tpt_Hierarchy_Tables_Are_Snake_Case()
    {
        using var context = TptModelContext.Create();

        var vehicle = context.Model.FindEntityType(typeof(Vehicle)).ShouldNotBeNull();
        var car = context.Model.FindEntityType(typeof(Car)).ShouldNotBeNull();

        vehicle.GetTableName().ShouldBe("vehicle");
        car.GetTableName().ShouldBe("car");
        car.FindProperty(nameof(Car.Doors)).ShouldNotBeNull().GetColumnName().ShouldBe("doors");
    }

    /// <summary>And a TPT derived type's explicit table name is rejected like any other.</summary>
    [Fact]
    public void Tpt_Derived_Table_Name_Outside_The_Convention_Is_Rejected()
    {
        using var context = WrongTptTableNameContext.Create();

        var error = Should.Throw<InvalidOperationException>(() => _ = context.Model);

        error.Message.ShouldContain("Car");
        error.Message.ShouldContain("WrongDerivedTable");
        error.Message.ShouldContain("'car'");
    }

    /// <summary>
    /// The column half of the same rule: a name set at convention priority — what
    /// EF's shared-table disambiguation does — is not a module's configuration, so
    /// it must yield to the snake_case convention for tables and columns alike.
    /// </summary>
    [Fact]
    public void Convention_Priority_Names_Do_Not_Fail_The_Convention()
    {
        using var context = ConventionPriorityNameContext.Create();

        var sample = context.Model.FindEntityType(typeof(ConventionSample)).ShouldNotBeNull();

        sample.GetTableName().ShouldBe("convention_sample");
        sample.FindProperty(nameof(ConventionSample.OrgId)).ShouldNotBeNull().GetColumnName().ShouldBe("org_id");
    }

    /// <summary>
    /// 00 §7: ids are UUIDv7 generated by the application. The 48-bit millisecond
    /// timestamp is the leading field (RFC 9562 §5.7), so ids created a millisecond
    /// apart strictly increase in the order PostgreSQL compares <c>uuid</c> — the
    /// canonical 16 bytes — and therefore also in canonical text order: an index on
    /// the column walks them in creation order (the test plan's insert-order index
    /// check). ⚠️ RFC 9562 §6.2 makes sub-millisecond monotonicity a SHOULD and
    /// .NET's <c>CreateVersion7</c> fills <c>rand_a</c> from the CSPRNG, so two ids
    /// for the same millisecond are unordered — measured on .NET 10, asserted here
    /// only as "same timestamp prefix, still distinct".
    /// </summary>
    [Fact]
    public void Uuidv7_Monotonic_Order()
    {
        var start = DateTimeOffset.UtcNow;
        var ids = new Guid[8];
        var canonical = new byte[ids.Length][];

        for (var index = 0; index < ids.Length; index++)
        {
            var created = start.AddMilliseconds(3 * index);
            ids[index] = Guid.CreateVersion7(created);
            ids[index].Version.ShouldBe(7);

            canonical[index] = new byte[16];
            ids[index].TryWriteBytes(canonical[index], bigEndian: true, out var written).ShouldBeTrue();
            written.ShouldBe(canonical[index].Length);

            // A v7 id carries — and sorts by — its creation millisecond.
            TimestampOf(canonical[index]).ShouldBe(created.ToUnixTimeMilliseconds());
        }

        for (var index = 1; index < ids.Length; index++)
        {
            // Guid field order, the canonical byte order and the canonical text
            // order must all agree with creation order.
            ids[index].CompareTo(ids[index - 1]).ShouldBePositive();
            canonical[index].AsSpan().SequenceCompareTo(canonical[index - 1]).ShouldBePositive();
            string.CompareOrdinal(
                ids[index].ToString("D", CultureInfo.InvariantCulture),
                ids[index - 1].ToString("D", CultureInfo.InvariantCulture)).ShouldBePositive();
        }

        // Same millisecond: the timestamp prefix is equal, the ids are still unique,
        // and their relative order is the random part — see the ⚠️ above.
        var sameMillisecond = new[] { Guid.CreateVersion7(start), Guid.CreateVersion7(start) };
        sameMillisecond[0].ShouldNotBe(sameMillisecond[1]);

        var first = new byte[16];
        var second = new byte[16];
        sameMillisecond[0].TryWriteBytes(first, bigEndian: true, out _).ShouldBeTrue();
        sameMillisecond[1].TryWriteBytes(second, bigEndian: true, out _).ShouldBeTrue();
        TimestampOf(first).ShouldBe(TimestampOf(second));
        first.AsSpan(0, 6).SequenceEqual(second.AsSpan(0, 6)).ShouldBeTrue();
    }

    /// <summary>The 48-bit millisecond timestamp a UUIDv7 carries in its first six canonical bytes (RFC 9562 §5.7).</summary>
    private static long TimestampOf(byte[] canonical)
    {
        long milliseconds = 0;
        for (var index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | canonical[index];
        }

        return milliseconds;
    }
}

/// <summary>The root of a TPT hierarchy: its own table, plus one per derived type.</summary>
internal abstract class Vehicle
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }
}

/// <summary>The derived half of the TPT hierarchy (QA-A-11).</summary>
internal sealed class Car : Vehicle
{
    public int Doors { get; set; }
}

/// <summary>The attribute spelling of the same mapping: <c>[Table]</c>/<c>[Column]</c>, both equal to the convention.</summary>
[Table("attribute_sample")]
internal sealed class AttributeSample
{
    public Guid Id { get; set; }

    [Column("org_id")]
    public Guid OrgId { get; set; }
}

/// <summary>The attribute spelling of a violation: a <c>[Table]</c> name outside the convention.</summary>
[Table("WrongAttributeTable")]
internal sealed class WrongAttributeSample
{
    public Guid Id { get; set; }
}

/// <summary>The attribute spelling of the other violation: a <c>[Column]</c> name outside the convention.</summary>
[Table("wrong_attribute_column_sample")]
internal sealed class WrongAttributeColumnSample
{
    public Guid Id { get; set; }

    [Column("OrgID")]
    public Guid OrgId { get; set; }
}

/// <summary>
/// A test-only entity covering the reserved column names of 00 §7 and the
/// <c>duration_ms</c> naming contract, mapped by the conventions alone.
/// </summary>
internal sealed class ConventionSample
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int Version { get; set; }

    public int DurationMs { get; set; }
}

/// <summary>
/// The sample mapped with explicit names that are the snake_case spelling of the
/// convention: the fix-1 acceptance case, exercised through
/// <see cref="EvidgateDbContext"/> so the conventions run last exactly as in
/// production. Every context here passes the Persistence assembly to
/// <c>UseTenantModel</c> (which requires one) instead of this test assembly, so the
/// model under test holds exactly the sample entity and no other test's
/// configuration can leak into it.
/// </summary>
internal sealed class ConventionModelContext(DbContextOptions<ConventionModelContext> options)
    : EvidgateDbContext(options)
{
    internal static ConventionModelContext Create() =>
        new(new DbContextOptionsBuilder<ConventionModelContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConventionSample>(sample =>
        {
            sample.ToTable("convention_sample");
            sample.Property(entity => entity.OrgId).HasColumnName("org_id");
        });

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>The fix-1 rejection case for tables: an explicit name outside the convention.</summary>
internal sealed class WrongTableNameContext(DbContextOptions<WrongTableNameContext> options)
    : EvidgateDbContext(options)
{
    internal static WrongTableNameContext Create() =>
        new(new DbContextOptionsBuilder<WrongTableNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConventionSample>().ToTable("WrongTableName");

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>The fix-1 rejection case for columns: an explicit name outside the convention.</summary>
internal sealed class WrongColumnNameContext(DbContextOptions<WrongColumnNameContext> options)
    : EvidgateDbContext(options)
{
    internal static WrongColumnNameContext Create() =>
        new(new DbContextOptionsBuilder<WrongColumnNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConventionSample>().Property(sample => sample.OrgId).HasColumnName("OrgID");

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>A TPT hierarchy, where every type owns a table the convention has to name.</summary>
internal sealed class TptModelContext(DbContextOptions<TptModelContext> options) : EvidgateDbContext(options)
{
    internal static TptModelContext Create() =>
        new(new DbContextOptionsBuilder<TptModelContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Vehicle>().UseTptMappingStrategy();
        modelBuilder.Entity<Car>();

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>A TPT hierarchy with an explicitly named derived table outside the convention.</summary>
internal sealed class WrongTptTableNameContext(DbContextOptions<WrongTptTableNameContext> options)
    : EvidgateDbContext(options)
{
    internal static WrongTptTableNameContext Create() =>
        new(new DbContextOptionsBuilder<WrongTptTableNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Vehicle>().UseTptMappingStrategy();
        modelBuilder.Entity<Car>().ToTable("WrongDerivedTable");

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>The attribute path of fix 1: <c>[Table]</c> and <c>[Column]</c> at DataAnnotation priority.</summary>
internal sealed class AttributeNameContext(DbContextOptions<AttributeNameContext> options) : EvidgateDbContext(options)
{
    internal static AttributeNameContext Create() =>
        new(new DbContextOptionsBuilder<AttributeNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AttributeSample>();

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>The attribute path of fix 1, rejected: a <c>[Table]</c> name outside the convention.</summary>
internal sealed class WrongAttributeNameContext(DbContextOptions<WrongAttributeNameContext> options)
    : EvidgateDbContext(options)
{
    internal static WrongAttributeNameContext Create() =>
        new(new DbContextOptionsBuilder<WrongAttributeNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<WrongAttributeSample>();

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>The attribute path of fix 1, rejected for a column: a <c>[Column]</c> name outside the convention.</summary>
internal sealed class WrongAttributeColumnContext(DbContextOptions<WrongAttributeColumnContext> options)
    : EvidgateDbContext(options)
{
    internal static WrongAttributeColumnContext Create() =>
        new(new DbContextOptionsBuilder<WrongAttributeColumnContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<WrongAttributeColumnSample>();

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// Names set at convention priority, the way EF's own conventions set them (the
/// shared-table disambiguation renames columns this way). They are not a module's
/// configuration and must be replaced by the project's convention.
/// </summary>
internal sealed class ConventionPriorityNameContext(DbContextOptions<ConventionPriorityNameContext> options)
    : EvidgateDbContext(options)
{
    internal static ConventionPriorityNameContext Create() =>
        new(new DbContextOptionsBuilder<ConventionPriorityNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entityType = (IConventionEntityType)modelBuilder.Entity<ConventionSample>().Metadata;
        entityType.SetTableName("ConventionTable", fromDataAnnotation: false);
        entityType.FindProperty(nameof(ConventionSample.OrgId))!
            .SetColumnName("ConventionOrg", fromDataAnnotation: false);

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// The shape every module context has: a <c>DbSet</c> property. EF's
/// <c>TableNameFromDbSetConvention</c> takes the property name for the table when
/// the entity type is added, so this context proves fix 1 does not mistake that
/// convention-set name for an explicit configuration.
/// </summary>
internal sealed class DbSetNameContext(DbContextOptions<DbSetNameContext> options) : EvidgateDbContext(options)
{
    /// <summary>Would become the table name "Samples" if the convention did not win (01.4.2-T3).</summary>
    public DbSet<ConventionSample> Samples { get; set; } = null!;

    internal static DbSetNameContext Create() =>
        new(new DbContextOptionsBuilder<DbSetNameContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(EvidgateDbContext).Assembly)
            .Options);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConventionSample>();

        base.OnModelCreating(modelBuilder);
    }
}
