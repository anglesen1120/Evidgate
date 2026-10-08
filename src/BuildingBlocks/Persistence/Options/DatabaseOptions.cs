using System.ComponentModel.DataAnnotations;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;

namespace Evidgate.BuildingBlocks.Persistence.Options;

/// <summary>
/// The single database setting both entry points bind (01.1.2-T1.1): the
/// application-role connection string, resolved through <see cref="ISecretProvider"/>
/// from <see cref="SecretNames.DatabaseConnectionString"/> (00 §5.7 S3:
/// environment/secret store only, never a committed file).
/// The Migrator uses its own <c>EVIDGATE_MIGRATOR__*</c> settings and owner role.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__DATABASE__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Database";

    /// <summary>
    /// Npgsql connection string for the <c>evidgate_app</c> role. The setter is
    /// internal on purpose: the configuration binder only writes public setters, so
    /// the value can only come from <see cref="DatabaseOptionsSecretPostConfigure"/>,
    /// which resolves it through <see cref="ISecretProvider"/>.
    /// </summary>
    [Required(ErrorMessage = SecretNames.DatabaseConnectionString + " is required")]
    [MinLength(1, ErrorMessage = SecretNames.DatabaseConnectionString + " must not be empty")]
    public string ConnectionString { get; internal set; } = string.Empty;
}
