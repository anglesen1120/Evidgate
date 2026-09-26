using System.ComponentModel.DataAnnotations;

namespace Evidgate.BuildingBlocks.Persistence.Options;

/// <summary>
/// The single database setting both entry points bind (01.1.2-T1.1): the
/// application-role connection string, from <c>EVIDGATE__DATABASE__CONNECTIONSTRING</c>
/// (00 §5.7 S3: environment/secret store only, never a committed file).
/// The Migrator uses its own <c>EVIDGATE_MIGRATOR__*</c> settings and owner role.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__DATABASE__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Database";

    /// <summary>Npgsql connection string for the <c>evidgate_app</c> role.</summary>
    [Required(ErrorMessage = "EVIDGATE__DATABASE__CONNECTIONSTRING is required")]
    [MinLength(1, ErrorMessage = "EVIDGATE__DATABASE__CONNECTIONSTRING must not be empty")]
    public string ConnectionString { get; init; } = string.Empty;
}
