#if EVIDGATE_MIGRATOR_ALIASED
extern alias migrator;
#endif

using System.Globalization;
using Npgsql;

#if EVIDGATE_MIGRATOR_ALIASED
using MigrationCommand = migrator::Evidgate.Migrator.MigrationCommand;
using MigratorExitCodes = migrator::Evidgate.Migrator.MigratorExitCodes;
using MigratorOptions = migrator::Evidgate.Migrator.MigratorOptions;
using MigratorRunner = migrator::Evidgate.Migrator.MigratorRunner;
#else
using Evidgate.Migrator;
#endif

namespace Evidgate.Testing;

/// <summary>
/// One migrated database per fixture instance that every test database is copied
/// from: the real migrations run once, and per-test creation becomes a PostgreSQL
/// <c>create database … template</c> instead of another full migration run.
/// </summary>
/// <remarks>
/// PostgreSQL refuses to copy a template that has open connections, so every
/// connection this type opens is unpooled and copies are serialised: a concurrent
/// <c>create database</c> from one template fails transiently with 55006.
/// </remarks>
internal sealed class TemplateDatabase : IDisposable
{
    private const string OwnerRole = "evidgate_owner";
    private const string MaintenanceDatabase = "postgres";

    private readonly Func<string, string> _superuserConnectionString;
    private readonly Func<string, string> _ownerConnectionString;
    private readonly string _migrationsPath;
    private readonly IReadOnlyList<string> _templateStatements;
    private readonly SemaphoreSlim _copyGate = new(1, 1);
    private readonly object _templateLock = new();
    private Task<string>? _template;

    private readonly string _templateName =
        "evidgate_tpl_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

    /// <param name="superuserConnectionString">Superuser connection string for the named database.</param>
    /// <param name="ownerConnectionString">Migration-role connection string for the named database.</param>
    /// <param name="migrationsPath">Directory of the real migration files.</param>
    /// <param name="templateStatements">Role statements run once, after migrations, on the template.</param>
    internal TemplateDatabase(
        Func<string, string> superuserConnectionString,
        Func<string, string> ownerConnectionString,
        string migrationsPath,
        IReadOnlyList<string> templateStatements)
    {
        ArgumentNullException.ThrowIfNull(superuserConnectionString);
        ArgumentNullException.ThrowIfNull(ownerConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsPath);
        ArgumentNullException.ThrowIfNull(templateStatements);

        _superuserConnectionString = superuserConnectionString;
        _ownerConnectionString = ownerConnectionString;
        _migrationsPath = migrationsPath;
        _templateStatements = templateStatements;
    }

    /// <summary>Releases the copy gate; call after the fixture has stopped creating databases.</summary>
    public void Dispose() => _copyGate.Dispose();

    /// <summary>Creates the named database as a copy of the migrated template.</summary>
    /// <param name="database">The database name; the caller's own per-test prefix applies.</param>
    internal async Task<string> CreateDatabaseAsync(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var template = await EnsureTemplateAsync().ConfigureAwait(false);

        await _copyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ExecuteSuperuserAsync(
                MaintenanceDatabase,
                $"create database \"{database}\" owner {OwnerRole} template \"{template}\"").ConfigureAwait(false);
        }
        finally
        {
            _copyGate.Release();
        }

        return database;
    }

    private Task<string> EnsureTemplateAsync()
    {
        Task<string> build;
        lock (_templateLock)
        {
            build = _template ??= BuildTemplateAsync();
        }

        return build;
    }

    private async Task<string> BuildTemplateAsync()
    {
        await ExecuteSuperuserAsync(
            MaintenanceDatabase,
            $"create database \"{_templateName}\" owner {OwnerRole}").ConfigureAwait(false);

        var outcome = MigratorRunner.Run(
            new MigratorOptions
            {
                ConnectionString = Unpooled(_ownerConnectionString(_templateName)),
                MigrationsPath = _migrationsPath,
            },
            MigrationCommand.Apply,
            new StringWriter(CultureInfo.InvariantCulture));

        if (outcome.ExitCode != MigratorExitCodes.Success)
        {
            throw new InvalidOperationException(
                $"the suite could not reach head on '{_templateName}' (exit {outcome.ExitCode}): "
                + string.Join(" | ", outcome.Messages));
        }

        foreach (var statement in _templateStatements)
        {
            await ExecuteSuperuserAsync(_templateName, statement).ConfigureAwait(false);
        }

        // Closing the template to connections keeps a stray session from blocking a
        // later copy; CREATE DATABASE still reads it.
        await ExecuteSuperuserAsync(
            MaintenanceDatabase,
            $"alter database \"{_templateName}\" is_template true allow_connections false").ConfigureAwait(false);

        return _templateName;
    }

    private async Task ExecuteSuperuserAsync(string database, string sql)
    {
        await using var connection =
            new NpgsqlConnection(Unpooled(_superuserConnectionString(database)));
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}
