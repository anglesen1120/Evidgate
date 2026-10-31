using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Migrator;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace Migrator.IntegrationTests;

/// <summary>
/// What the <c>seed-dev --with-token</c> test classes share: one seeded database per test, the environment a run reads,
/// the in-process run itself, and the readers that pull the token and the policy out of the transcript.
/// </summary>
internal sealed class SeedDevTokenHarness(PostgresMigratorFixture fixture)
{
    /// <summary>The stdout prefix of the one-time secret.</summary>
    internal const string DevTokenLine = "EVIDGATE_DEV_TOKEN=";

    /// <summary>The recovery instruction a rerun prints, verbatim: the command is part of the contract.</summary>
    internal const string DecidedRecoveryText =
        "a live 'dev-seed' ingest token already exists; its secret was shown once and cannot be shown again. " +
        "To get a new one run: scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token";

    internal PostgresMigratorFixture Fixture => fixture;

    internal async Task<(string Database, string Pepper)> SeededDatabaseAsync()
    {
        var database = await MigratedDatabaseAsync();

        return (database, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>
    /// A fresh database carrying the repository's migrations, with the application roles able to log in. Every
    /// connection string this harness hands out is unpooled: each seeded run opens its own app-role data source and
    /// creates its own database, so pooled connections that stay idle for the pool's idle lifetime would hold the
    /// container's remaining slots (53300) against the other classes in the collection.
    /// </summary>
    internal async Task<string> MigratedDatabaseAsync()
    {
        var database = await fixture.CreateDatabaseAsync();
        MigratorHarness.Run(PostgresMigratorFixture.RepoMigrationsPath, Unpooled(fixture.OwnerConnectionString(database)))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        await fixture.EnableApplicationLoginsAsync(database);

        return database;
    }

    internal RunResult RunWithToken(
        string database,
        string pepper,
        string? environment = "Development",
        string? appConnectionString = null,
        bool omitPepper = false,
        string? ownerConnectionString = null) =>
        Run(
            ["seed-dev", "--with-token"],
            Variables(database, pepper, environment, appConnectionString, omitPepper, ownerConnectionString));

    /// <summary>The environment one seeded run reads: owner and app connections, the environment and the pepper.</summary>
    internal Dictionary<string, string?> Variables(
        string database,
        string pepper,
        string? environment = "Development",
        string? appConnectionString = null,
        bool omitPepper = false,
        string? ownerConnectionString = null)
    {
        var variables = NoEnvironment();
        variables[MigratorOptions.ConnectionStringVariable] =
            ownerConnectionString ?? Unpooled(fixture.OwnerConnectionString(database));
        variables[DevSeed.EnvironmentVariable] = environment;
        variables[SecretNames.DatabaseConnectionString] =
            appConnectionString ?? Unpooled(fixture.AppConnectionString(database));

        if (!omitPepper)
        {
            variables[SecretNames.CiPepper] = pepper;
        }

        return variables;
    }

    internal static Dictionary<string, string?> NoEnvironment() => new(StringComparer.Ordinal);

    /// <summary>The repository's <c>contracts/</c> directory, the sibling of the resolved <c>migrations/</c>.</summary>
    internal static string ContractsDirectory() => Path.Combine(
        Path.GetDirectoryName(PostgresMigratorFixture.RepoMigrationsPath)
            ?? throw new InvalidOperationException("the repository root is the parent of migrations/"),
        "contracts");

    /// <summary>
    /// The same connection string with pooling off: a connection is released the moment it is disposed, so the tests
    /// cannot leave idle sessions on the shared container that the other classes then fail to get (53300).
    /// </summary>
    internal static string Unpooled(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString)
    {
        Pooling = false,
    }.ConnectionString;

    internal static RunResult Run(string[] args, IReadOnlyDictionary<string, string?> variables)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        return new(MigratorHost.Run(args, output, error, variables), output.ToString(), error.ToString());
    }

    internal async Task AssertNothingWrittenAsync(string database)
    {
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.organization")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.project")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.policy_revision")).ShouldBe(0);
    }

    internal async Task<(Guid Id, string Hash)> PolicyRevisionAsync(string database) => (
        Guid.Parse(await fixture.ScalarAsync<string>(database, "select id::text from public.policy_revision")),
        await fixture.ScalarAsync<string>(database, "select hash from public.policy_revision"));

    internal static string TokenOf(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Single(line => line.StartsWith(DevTokenLine, StringComparison.Ordinal))[DevTokenLine.Length..];

    internal static (string Prefix, string Secret) Split(string token)
    {
        // The secret half is base64url, so it can itself contain '_': split into three, never on every underscore.
        var parts = token.Split('_', 3);

        return (parts[1], parts[2]);
    }

    internal static string ExpectedHmac(string pepper, string secret) => Convert.ToHexStringLower(
        HMACSHA256.HashData(Convert.FromBase64String(pepper), Encoding.UTF8.GetBytes(secret)));

    internal static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    internal sealed record RunResult(int ExitCode, string Output, string Error);

    /// <summary>Captures every formatted log line, so a test can prove what does and does not reach a logger.</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        internal IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }

    /// <summary>Fails on the one-time secret line and echoes that line, and the pepper, into the exception.</summary>
    internal sealed class SecretEchoingWriter(string pepper) : StringWriter(CultureInfo.InvariantCulture)
    {
        public override Task WriteLineAsync(string? value) =>
            value is not null && value.StartsWith(DevTokenLine, StringComparison.Ordinal)
                ? Task.FromException(new IOException($"could not write '{value}' under pepper {pepper}"))
                : base.WriteLineAsync(value);
    }
}
