using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The model conventions of 01.4.2-T1.3 (00 §5.5 C4, §7), implemented here
/// instead of taking a naming-convention package (00 §5.2 keeps dependencies
/// explicit):
/// <list type="bullet">
///   <item>tables are the snake_case, singular CLR type name — for a hierarchy root
///   and for every type of a TPT/TPC hierarchy, each of which owns a table; a TPH
///   derived type shares its root's table and takes that name — and columns are the
///   snake_case property name: a module cannot map itself outside the contract, and
///   an explicitly configured name that disagrees with the convention fails the
///   model build instead of being silently overwritten;</item>
///   <item>a <c>version</c> column is the optimistic-concurrency token and is
///   store-generated on update — the migration trigger
///   <c>set_updated_at_and_version</c> increments it (00 §7);</item>
///   <item>a <c>Guid</c> <c>id</c> is application-generated: the caller's value
///   always wins, and a key the caller left unset is filled by the client-side
///   <see cref="UuidV7ValueGenerator"/> (<c>Guid.CreateVersion7()</c>, RFC 9562
///   §5.7) — never by a database default and never with the zero Guid;</item>
///   <item><c>created_at</c>/<c>updated_at</c> are UTC <c>timestamptz</c>
///   (00 §5.4.4, §7) — the Npgsql provider maps both CLR timestamp types there, and
///   the catalogue gate on the migrated column is <c>SchemaConventionTests</c>'
///   audit-column rule, so no store type is pinned here (that would silently
///   override an explicit one);</item>
///   <item>durations are <c>duration_ms</c> — integer milliseconds from the
///   runner's monotonic clock (00 §5.4.4) — so a <c>DurationMs</c> property
///   always lands on that fixed column name.</item>
/// </list>
/// No lazy loading is configured, and EF migrations are never generated (C4).
/// </summary>
internal static class TenantModelConventions
{
    private const string IdColumn = "id";
    private const string VersionColumn = "version";

    /// <summary>Applies the conventions to every mapped entity type.</summary>
    internal static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (OwnsItsOwnTable(entityType))
            {
                ApplyTableName(entityType);
            }

            foreach (var property in entityType.GetProperties())
            {
                ApplyColumnName(entityType, property);
            }
        }
    }

    /// <summary>
    /// True when the entity type maps to a table of its own: a hierarchy root, or any
    /// type of a TPT/TPC hierarchy, where every type owns a table. Owned and
    /// shared-type (property bag) entities map onto their owner's table, and a TPH
    /// derived type maps onto its root's, so neither is renamed here (00 §7, §5.5 C4).
    /// </summary>
    private static bool OwnsItsOwnTable(IReadOnlyEntityType entityType)
    {
        if (entityType.IsOwned() || entityType.HasSharedClrType)
        {
            return false;
        }

        return entityType.BaseType is null
            || entityType.GetMappingStrategy() is RelationalAnnotationNames.TpcMappingStrategy
                or RelationalAnnotationNames.TptMappingStrategy;
    }

    /// <summary>
    /// The table name is the snake_case, singular CLR type name. An explicit name
    /// that agrees with it is accepted; one that disagrees is a model-build failure
    /// naming the entity and the expected name — the alternative (overwriting it)
    /// would leave a module configured against a table that does not exist, and the
    /// error would only surface as a missing-relation at runtime (00 §5.5 C4).
    /// </summary>
    private static void ApplyTableName(IMutableEntityType entityType)
    {
        var conventional = SnakeCase(entityType.ClrType.Name);

        // The configuration source decides, not the annotation's presence: EF's own
        // conventions set names too — TableNameFromDbSetConvention maps the DbSet
        // property name (`DbSet<Project> Projects` ⇒ "Projects") when an entity type
        // is added — and such a name has to yield to this convention instead of
        // failing the build. Only Explicit and DataAnnotation come from a module.
        if (((IConventionEntityType)entityType).GetTableNameConfigurationSource()
            is not (ConfigurationSource.Explicit or ConfigurationSource.DataAnnotation))
        {
            entityType.SetTableName(conventional);
            return;
        }

        if (!string.Equals(entityType.GetTableName(), conventional, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The entity '{entityType.ClrType.Name}' is configured with the table name '{entityType.GetTableName()}'. "
                + $"Table names are the snake_case singular CLR type name (00 §7, §5.5 C4): remove the explicit "
                + $"name or use '{conventional}'.");
        }
    }

    /// <summary>
    /// The column name is the snake_case property name, and the reserved names
    /// carry their 00 §7 semantics. As with tables, an explicit name that disagrees
    /// with the convention is a model-build failure, not an overwrite, and a name a
    /// convention of EF's own set (the shared-table disambiguation) still yields.
    /// </summary>
    private static void ApplyColumnName(IMutableEntityType entityType, IMutableProperty property)
    {
        var column = SnakeCase(property.Name);

        if (((IConventionProperty)property).GetColumnNameConfigurationSource()
            is not (ConfigurationSource.Explicit or ConfigurationSource.DataAnnotation))
        {
            property.SetColumnName(column);
        }
        else if (!string.Equals(property.GetColumnName(), column, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The property '{entityType.ClrType.Name}.{property.Name}' is configured with the column name "
                + $"'{property.GetColumnName()}'. Column names are the snake_case property name (00 §7, §5.5 C4): "
                + $"remove the explicit name or use '{column}'.");
        }

        if (column == VersionColumn)
        {
            property.IsConcurrencyToken = true;

            // 00 §7, M1W3-01: the database owns the increment — every business
            // table carries the `set_updated_at_and_version` BEFORE UPDATE trigger
            // (migration 0004 for `organization`, 0005 for `project`). The token is
            // therefore store-generated on update: EF writes it into neither the
            // SET list nor the entity, and reads the server's new value back, so a
            // second tracked update in the same scope matches the row. A table
            // whose migration has not attached the trigger simply keeps the value
            // it had — exactly what the explicit write did before.
            property.ValueGenerated = ValueGenerated.OnUpdate;
        }
        else if (column == IdColumn && property.ClrType == typeof(Guid))
        {
            // 00 §7, T3: UUIDv7 ids come from the application. A caller's explicit
            // id wins (EF only generates when the key is unset), and a caller that
            // forgets still gets a v7 id — never the zero Guid a `Never` mapping
            // would insert verbatim. ValueGenerated.OnAdd with a client-side
            // generator tells EF the value is produced in the application, so no
            // store value is read back.
            property.ValueGenerated = ValueGenerated.OnAdd;
            property.SetValueGeneratorFactory((_, _) => UuidV7ValueGenerator.Instance);
        }
    }

    /// <summary>Client-side UUIDv7 generator (RFC 9562 §5.7) for `id` keys the caller leaves unset.</summary>
    private sealed class UuidV7ValueGenerator : ValueGenerator<Guid>
    {
        internal static readonly UuidV7ValueGenerator Instance = new();

        public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();

        public override bool GeneratesTemporaryValues => false;
    }

    /// <summary>
    /// snake_case over a CLR name: an upper-case letter starts a new word when it
    /// follows a lower-case letter or a digit, or when it is the last letter of an
    /// acronym (<c>OrgId</c> → <c>org_id</c>, <c>CreatedAt</c> → <c>created_at</c>,
    /// <c>HTTPStatus</c> → <c>http_status</c>, <c>Sha256</c> → <c>sha256</c>).
    /// </summary>
    internal static string SnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (char.IsUpper(current))
            {
                if (index > 0 && StartsWord(name, index))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private static bool StartsWord(string name, int index)
    {
        var previous = name[index - 1];
        if (char.IsLower(previous) || char.IsDigit(previous))
        {
            return true;
        }

        // An acronym keeps its run together: only an upper-case letter followed by
        // a lower-case one ends it ("HTTPStatus" → http | status).
        return char.IsUpper(previous)
            && index + 1 < name.Length
            && char.IsLower(name[index + 1]);
    }
}
