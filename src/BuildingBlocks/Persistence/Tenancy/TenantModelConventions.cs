using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The model conventions of 01.4.2-T1.3 (00 §5.5 C4, §7), implemented here
/// instead of taking a naming-convention package (00 §5.2 keeps dependencies
/// explicit):
/// <list type="bullet">
///   <item>tables are the snake_case, singular CLR type name and columns are the
///   snake_case property name — a module cannot map itself outside the contract;</item>
///   <item>a <c>version</c> column is the optimistic-concurrency token and is
///   store-generated on update — the migration trigger
///   <c>set_updated_at_and_version</c> increments it (00 §7);</item>
///   <item>a <c>Guid</c> <c>id</c> is application-generated, so EF never fills in a
///   v4 Guid in place of the caller's <c>Guid.CreateVersion7()</c>.</item>
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
            // Owned types and shared-type (property bag) entities map onto their
            // owner's table; only a hierarchy root owns a table of its own.
            if (entityType.BaseType is null && !entityType.IsOwned() && !entityType.HasSharedClrType)
            {
                entityType.SetTableName(SnakeCase(entityType.ClrType.Name));
            }

            foreach (var property in entityType.GetProperties())
            {
                var column = SnakeCase(property.Name);
                property.SetColumnName(column);

                if (column == VersionColumn)
                {
                    property.IsConcurrencyToken = true;

                    // 00 §7, M1W3-01: the database owns the increment — every
                    // business table carries the `set_updated_at_and_version`
                    // BEFORE UPDATE trigger (migration 0004 for `organization`,
                    // 0005 for `project`). The token is therefore
                    // store-generated on update: EF writes it into neither the
                    // SET list nor the entity, and reads the server's new value
                    // back, so a second tracked update in the same scope matches
                    // the row. A table whose migration has not attached the
                    // trigger simply keeps the value it had — exactly what the
                    // explicit write did before.
                    property.ValueGenerated = ValueGenerated.OnUpdate;
                }
                else if (column == IdColumn && property.ClrType == typeof(Guid))
                {
                    property.ValueGenerated = ValueGenerated.Never;
                }
            }
        }
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
