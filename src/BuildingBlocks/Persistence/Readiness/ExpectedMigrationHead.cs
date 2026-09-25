using System.Reflection;

namespace Evidgate.BuildingBlocks.Persistence.Readiness;

/// <summary>
/// The migration journal head this build expects: the lexically greatest
/// <c>migrations/*.sql</c> file name, baked into the assembly as
/// <see cref="MetadataKey"/> by the MSBuild target in this project (01.1.2-T1.3).
/// A new migration file therefore changes the readiness gate with no code edit,
/// and the constant stays identical to what <c>Evidgate.Migrator</c> records in
/// <c>public.schema_migration.name</c> (name including the <c>.sql</c> suffix).
/// </summary>
public static class ExpectedMigrationHead
{
    /// <summary>Assembly metadata key carrying the baked head.</summary>
    public const string MetadataKey = "ExpectedMigrationHead";

    /// <summary>
    /// Baked value when the build found no <c>migrations/*.sql</c> file. It can
    /// never equal a journal row, so a build without migrations is never ready.
    /// Must match the literal in Evidgate.BuildingBlocks.Persistence.csproj.
    /// </summary>
    public const string NoMigrationsHead = "(none)";

    /// <summary>The expected head, read once from this assembly's metadata.</summary>
    public static string Value { get; } = Read();

    private static string Read()
    {
        var baked = typeof(ExpectedMigrationHead).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == MetadataKey)
            ?.Value;

        return string.IsNullOrEmpty(baked) ? NoMigrationsHead : baked;
    }
}
