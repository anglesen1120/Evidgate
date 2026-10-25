namespace Evidgate.BuildingBlocks.SharedKernel.Secrets;

/// <summary>
/// The names of every secret the platform resolves (10.1.1-T2). The values are the
/// environment-variable names the configuration matrix (<c>infra/config-matrix.json</c>)
/// already declares, so nothing is renamed; <see cref="ToConfigurationKey"/> maps one
/// to the <c>IConfiguration</c> spelling the environment-variables provider uses.
/// </summary>
/// <remarks>
/// This type is the only place a secret key is spelled as a literal. Every other
/// source file references these constants, which is what the architecture guard
/// (<c>Architecture.Tests</c>) enforces.
/// </remarks>
public static class SecretNames
{
    /// <summary>Application-role database connection string (<c>evidgate_app</c>).</summary>
    public const string DatabaseConnectionString = "EVIDGATE__DATABASE__CONNECTIONSTRING";

    /// <summary>Sweep-role database connection string (<c>evidgate_worker_sched</c>, TL T80).</summary>
    public const string TasksSweepConnectionString = "EVIDGATE__TASKS__SWEEPCONNECTIONSTRING";

    /// <summary>Base64 HS256 key that signs the minted <c>ci_producer</c> JWTs (D7).</summary>
    public const string CiSigningKey = "EVIDGATE__CI__SIGNINGKEY";

    /// <summary>Base64 current pepper for ingest-token and binding-challenge HMACs (T5, D-A4).</summary>
    public const string CiPepper = "EVIDGATE__CI__PEPPER";

    /// <summary>Base64 previous pepper, kept while tokens made with it are still valid.</summary>
    public const string CiPreviousPepper = "EVIDGATE__CI__PREVIOUSPEPPER";

    /// <summary>Owner-role connection string of the deploy-time Migrator.</summary>
    public const string MigratorConnectionString = "EVIDGATE_MIGRATOR__CONNECTIONSTRING";

    /// <summary>Access key of the object-storage identity (06.1.2-T1).</summary>
    public const string ObjectAccessKey = "EVIDGATE__OBJECT__ACCESSKEY";

    /// <summary>Secret key of the object-storage identity (06.1.2-T1).</summary>
    public const string ObjectSecretKey = "EVIDGATE__OBJECT__SECRETKEY";

    /// <summary>Client secret of the Google OIDC login client (02.3.1-T1.2); the client id is plain configuration.</summary>
    public const string OidcGoogleClientSecret = "EVIDGATE__OIDC__GOOGLE__CLIENTSECRET";

    private static readonly string[] Names =
    [
        DatabaseConnectionString,
        TasksSweepConnectionString,
        CiSigningKey,
        CiPepper,
        CiPreviousPepper,
        MigratorConnectionString,
        ObjectAccessKey,
        ObjectSecretKey,
        OidcGoogleClientSecret,
    ];

    /// <summary>Every known secret name; a name outside this list is rejected by the provider.</summary>
    public static IReadOnlyList<string> All => Names;

    /// <summary>
    /// Maps a secret name to its <c>IConfiguration</c> key: the environment-variables
    /// provider turns <c>__</c> into <c>:</c>, so <c>EVIDGATE__CI__PEPPER</c> is read as
    /// <c>EVIDGATE:CI:PEPPER</c> (case is kept; configuration keys are case-insensitive).
    /// </summary>
    /// <param name="name">A secret name, as declared by this type.</param>
    public static string ToConfigurationKey(string name) =>
        name.Replace("__", ":", StringComparison.Ordinal);
}
