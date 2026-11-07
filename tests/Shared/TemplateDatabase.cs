#if EVIDGATE_MIGRATOR_ALIASED
extern alias migrator;
#endif

using System.Globalization;
using System.Text.RegularExpressions;
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
/// from: the real migrations run once — or, when the caller names a database it already
/// brought to head, not at all — and per-test creation becomes a PostgreSQL
/// <c>create database … template</c> instead of another full migration run.
/// </summary>
/// <remarks>
/// PostgreSQL refuses to copy a template that has open connections, so every
/// connection this type opens is unpooled. The copy gate serializes concurrent
/// copies from the same template, which would otherwise fail transiently with 55006.
/// </remarks>
internal sealed partial class TemplateDatabase : IDisposable
{
    private const string OwnerRole = "evidgate_owner";
    private const string MaintenanceDatabase = "postgres";

    private readonly Func<string, string> _superuserConnectionString;
    private readonly Func<string, string> _ownerConnectionString;
    private readonly string _migrationsPath;
    private readonly IReadOnlyList<string> _templateStatements;
    private readonly string? _copyFrom;
    private readonly SemaphoreSlim _copyGate = new(1, 1);
    private readonly object _templateLock = new();
    private Task<string>? _template;

    private readonly string _templateName =
        "evidgate_tpl_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

    /// <param name="superuserConnectionString">Superuser connection string for the named database.</param>
    /// <param name="ownerConnectionString">Migration-role connection string for the named database.</param>
    /// <param name="migrationsPath">Directory of the real migration files.</param>
    /// <param name="templateStatements">Role statements run once, after migrations, on the template.</param>
    /// <param name="copyFrom">
    /// A database the caller already brought to head, copied instead of running the migrations a
    /// second time. It must have no session open: <c>CREATE DATABASE … TEMPLATE</c> refuses one.
    /// </param>
    internal TemplateDatabase(
        Func<string, string> superuserConnectionString,
        Func<string, string> ownerConnectionString,
        string migrationsPath,
        IReadOnlyList<string> templateStatements,
        string? copyFrom = null)
    {
        ArgumentNullException.ThrowIfNull(superuserConnectionString);
        ArgumentNullException.ThrowIfNull(ownerConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsPath);
        ArgumentNullException.ThrowIfNull(templateStatements);

        if (copyFrom is not null)
        {
            EnsureGeneratedName(copyFrom, nameof(copyFrom));
        }

        _superuserConnectionString = superuserConnectionString;
        _ownerConnectionString = ownerConnectionString;
        _migrationsPath = migrationsPath;
        _templateStatements = templateStatements;
        _copyFrom = copyFrom;
    }

    /// <summary>Releases the copy gate; call after the fixture has stopped creating databases.</summary>
    public void Dispose() => _copyGate.Dispose();

    /// <summary>
    /// Drops the migrated template database. Only a reused server needs this: a container is discarded
    /// whole, while a server that outlives the run would otherwise keep the template forever. The
    /// <c>is_template</c> flag refuses a plain drop, so it comes off first.
    /// </summary>
    internal async Task DropAsync()
    {
        if (_template is null)
        {
            return;
        }

        await DropTemplateDatabaseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a scratch database; idempotent, and a failure never fails a run because
    /// the container is discarded whole.
    /// </summary>
    /// <param name="database">The database name, as <see cref="CreateDatabaseAsync"/> returned it.</param>
    internal async Task DropDatabaseAsync(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        EnsureGeneratedName(database, nameof(database));

        try
        {
            await ExecuteSuperuserAsync(
                MaintenanceDatabase,
                $"drop database if exists \"{database}\" with (force)").ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // A leftover scratch database never fails a run.
        }
    }

    /// <summary>Creates the named database as a copy of the migrated template.</summary>
    /// <param name="database">The database name; the caller's own per-test prefix applies.</param>
    internal async Task<string> CreateDatabaseAsync(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        EnsureGeneratedName(database, nameof(database));

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

    // Every name this type splices into SQL is generated, so only that ASCII shape is accepted.
    // \A…\z, not ^…$: $ also matches before a terminal newline.
    [GeneratedRegex(@"\A[A-Za-z0-9_]+\z")]
    private static partial Regex GeneratedName();

    /// <summary>Refuses any name that is not the generated shape, before SQL is built from it.</summary>
    private static void EnsureGeneratedName(string database, string parameterName)
    {
        if (!GeneratedName().IsMatch(database))
        {
            throw new ArgumentException(
                $"'{database}' is not a generated database name: letters, digits and '_' only.",
                parameterName);
        }
    }

    /// <summary>Builds the template once per process; concurrent callers share the one build.</summary>
    internal Task<string> EnsureTemplateAsync()
    {
        Task<string> build;
        lock (_templateLock)
        {
            // A failed migration is retried by the next copy instead of failing forever.
            if (_template is { IsFaulted: true } or { IsCanceled: true })
            {
                _template = null;
            }

            build = _template ??= BuildTemplateAsync();
        }

        return build;
    }

    private async Task<string> BuildTemplateAsync()
    {
        try
        {
            if (_copyFrom is { } source)
            {
                // The source is already at head and has no session open, so one copy is the
                // whole build: the caller ran the migrations once instead of twice.
                await ExecuteSuperuserAsync(
                    MaintenanceDatabase,
                    $"create database \"{_templateName}\" owner {OwnerRole} template \"{source}\"").ConfigureAwait(false);
            }
            else
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
        catch
        {
            // The retry reuses this name, so the half-built database has to go first;
            // it may exist with or without the is_template flag.
            await DropTemplateDatabaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Removes the template database of this instance: the flag first (a plain drop is
    /// refused while it is set), then the database itself. Both steps are idempotent and
    /// never fail the run.
    /// </summary>
    private async Task DropTemplateDatabaseAsync()
    {
        try
        {
            await ExecuteSuperuserAsync(
                MaintenanceDatabase,
                $"alter database \"{_templateName}\" is_template false").ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Not a template database (or not created yet): the drop below is the check.
        }

        await DropDatabaseAsync(_templateName).ConfigureAwait(false);
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

    /// <summary>
    /// The same connection string with pooling off: a template copy refuses a source database
    /// that still has a session, and a pooled connection outlives the close that ends its block.
    /// </summary>
    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}
