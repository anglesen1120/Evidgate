using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Policy.Application.Commands;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// <c>seed-dev --with-token</c>: after the synthetic tenant exists, mints one dev ingest token and one sample policy
/// revision through the Identity and Policy commands, under the same pipeline and tenant scope a request runs under.
/// </summary>
/// <remarks>
/// The plaintext is printed once, on its own <c>EVIDGATE_DEV_TOKEN=…</c> line, and reaches no logger, exception
/// message, file or error writer: only the HMAC and the public prefix are stored. A rerun prints the recovery
/// instruction instead, because a stored token's secret cannot be read again.
/// </remarks>
internal static class DevSeedToken
{
    /// <summary>The line a rerun prints instead of a secret, naming the one recovery that works without a login.</summary>
    internal static readonly string RerunNote =
        $"a live '{DevSeed.TokenName}' ingest token already exists; its secret was shown once and cannot be shown again. " +
        "To get a new one run: scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token";

    /// <summary>The one-time notice printed under the secret; the token itself is on the line above it.</summary>
    internal const string ShownOnceNote =
        "seed-dev: the token above is shown once and is not stored — capture it before this output is lost";

    /// <summary>
    /// The sample <c>policy-revision.v1</c> body: the two suites a run must cover and the flaky budget. Deliberately
    /// minimal and fixed, so the content address is stable and a rerun replays the stored revision instead of appending.
    /// </summary>
    private const string SamplePolicyBody = """{"flaky_budget":3,"required_suites":["smoke","regression"]}""";

    /// <summary>Applies seed part 2; returns the process exit code.</summary>
    /// <param name="options">Owner-role connection and paths (the tenant is written as the owner first).</param>
    /// <param name="environment">Environment snapshot; null reads the process environment.</param>
    /// <param name="output">Where progress goes, including the one-time secret.</param>
    /// <param name="error">Where a refusal or failure goes; never the secret.</param>
    /// <param name="loggerProvider">An extra log sink, for tests that assert the secret reaches no logger.</param>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The command line is synchronous end to end; the async flow runs to completion before the exit code is returned, exactly as the sync secret read does.")]
    internal static int Run(
        MigratorOptions options,
        IReadOnlyDictionary<string, string?>? environment,
        TextWriter output,
        TextWriter error,
        ILoggerProvider? loggerProvider = null)
    {
        if (ContractsDirectory(options.MigrationsPath) is not { } contractsDirectory)
        {
            error.WriteLine(
                $"E seed-dev --with-token refused: contracts/schemas was not found next to '{options.MigrationsPath}'; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }

        // The minted secret is handed back here so a failure message that quotes it can be redacted.
        var mintedSecret = new MintedSecret();

        try
        {
            return RunAsync(environment, contractsDirectory, options, output, error, loggerProvider, mintedSecret)
                .GetAwaiter()
                .GetResult();
        }
        catch (OptionsValidationException exception)
        {
            // Options validation names keys, never values, so its failures are safe to print as they are.
            error.WriteLine("E seed-dev --with-token failed: " + string.Join("; ", exception.Failures) + "; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }
        catch (NpgsqlException exception)
        {
            error.WriteLine("E seed-dev --with-token failed: " + Redacted(exception.Message, options, environment, mintedSecret.Value));

            return exception.IsTransient || exception.InnerException is System.Net.Sockets.SocketException
                ? MigratorExitCodes.DatabaseUnavailable
                : MigratorExitCodes.MigrationFailed;
        }
        catch (Exception exception)
        {
            // The seed handles a secret: a message is printed only after the redactor has run over it.
            error.WriteLine("E seed-dev --with-token failed: " + Redacted(exception.Message, options, environment, mintedSecret.Value));

            return MigratorExitCodes.MigrationFailed;
        }
    }

    /// <summary>
    /// The only path that composes the modules: refuses (writing nothing) unless the app-role data source and a usable
    /// pepper are both configured, writes the tenant as the owner role, then mints the policy and the token under one
    /// advisory-lock guard so two concurrent first runs cannot both mint.
    /// </summary>
    private static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string?>? environment,
        string contractsDirectory,
        MigratorOptions options,
        TextWriter output,
        TextWriter error,
        ILoggerProvider? loggerProvider,
        MintedSecret mintedSecret)
    {
        await using var provider = DevSeedServices.Compose(environment, contractsDirectory, loggerProvider);

        // Resolving the data source validates EVIDGATE__DATABASE__CONNECTIONSTRING and the evidgate_app role pin, so a
        // missing or wrong-role app connection fails closed here — before the owner-role tenant write below.
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        var appHost = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host;
        if (!DevSeed.IsLocalHost(appHost))
        {
            await error.WriteLineAsync($"E seed-dev refused: the application database host '{appHost}' is not local or a compose service name; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }

        if (!PepperConfigured(provider, out var pepperRefusal))
        {
            await error.WriteLineAsync(pepperRefusal).ConfigureAwait(false);

            return MigratorExitCodes.InvalidInvocation;
        }

        // The dev user's membership is what the permission evaluator reads, so the tenant is always written first —
        // idempotently, exactly as the plain seed does.
        var seeded = DevSeed.WriteTenant(options, output, error);
        if (seeded != MigratorExitCodes.Success)
        {
            return seeded;
        }

        await using var guard = await SeedGuard.AcquireAsync(dataSource).ConfigureAwait(false);

        // The synthetic caller exists only here: inside the guard, after the refusals, for the two commands below.
        using var tenantContext = provider.GetRequiredService<IRequestTenantContextSetter>()
            .Publish(DevSeed.OrgId, DevSeed.ProjectId);
        using var caller = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Member(DevSeed.UserId, DevSeed.UserEmail));

        var mediator = provider.GetRequiredService<IMediator>();

        // The policy is part of every run, not of the first one: an existing token skips only the mint, so a stack whose
        // policy revision was never seeded (or was seeded by a run that predates it) still ends up with one.
        var policy = await WriteSamplePolicyAsync(mediator, output, error).ConfigureAwait(false);
        if (policy != MigratorExitCodes.Success)
        {
            return policy;
        }

        if (await FindLiveTokenAsync(provider).ConfigureAwait(false) is not null)
        {
            await output.WriteLineAsync(RerunNote).ConfigureAwait(false);

            return MigratorExitCodes.Success;
        }

        return await MintTokenAsync(mediator, output, error, mintedSecret).ConfigureAwait(false);
    }

    /// <summary>
    /// False, with the refusal text, unless a current pepper is configured and usable. The pepper is read the way the
    /// hosts read it — through <see cref="ISecretProvider"/> and the module's own <see cref="CiSecrets"/> — so an
    /// unusable value is refused here instead of a 503 from the handler after the tenant has been written.
    /// </summary>
    private static bool PepperConfigured(IServiceProvider provider, out string refusal)
    {
        var pepper = provider.GetRequiredService<ISecretProvider>().GetSecret(SecretNames.CiPepper);
        if (string.IsNullOrWhiteSpace(pepper))
        {
            refusal = $"E seed-dev --with-token refused: {SecretNames.CiPepper} is not configured, so the dev ingest token cannot be hashed; nothing was written";

            return false;
        }

        // CiSecrets reports only whether the value decoded; a value that is too short fails the module's own options
        // validator, which runs on this resolution and throws OptionsValidationException — refused before any write.
        if (!provider.GetRequiredService<CiSecrets>().CanHash)
        {
            refusal = $"E seed-dev --with-token refused: {SecretNames.CiPepper} is not usable ({CiSecrets.MinKeyBytes} bytes of base64 are required); nothing was written";

            return false;
        }

        refusal = string.Empty;

        return true;
    }

    /// <summary>The dev project's one sample revision, through the real command; a rerun replays it (200) instead of appending.</summary>
    private static async Task<int> WriteSamplePolicyAsync(IMediator mediator, TextWriter output, TextWriter error)
    {
        var appended = await mediator
            .Send(new CreatePolicyRevisionCommand(DevSeed.ProjectId, Encoding.UTF8.GetBytes(SamplePolicyBody), IfMatch: null))
            .ConfigureAwait(false);

        if (!appended.IsSuccess)
        {
            await error.WriteLineAsync($"E seed-dev: the sample policy revision was refused ({appended.Error!.Code}): {appended.Error.Message}");

            return MigratorExitCodes.MigrationFailed;
        }

        var revision = appended.Value;
        var outcome = appended.Status == 201 ? "created" : "already present";
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"seed-dev: sample policy revision {revision.RevisionNo} {revision.Hash} ({outcome})"));

        return MigratorExitCodes.Success;
    }

    /// <summary>Mints the dev token and prints its secret once, on stdout; a failed mint writes nothing at all.</summary>
    private static async Task<int> MintTokenAsync(
        IMediator mediator,
        TextWriter output,
        TextWriter error,
        MintedSecret mintedSecret)
    {
        // No expiry: a dev token that silently expired would break a long-lived local stack, and the rerun note is the
        // recovery for a lost secret. Scopes stay the handler's default, so the row matches what is printed below.
        var minted = await mediator
            .Send(new CreateIngestTokenCommand(DevSeed.ProjectId, DevSeed.TokenName, Scopes: null, ExpiresAt: null))
            .ConfigureAwait(false);

        if (!minted.IsSuccess)
        {
            await error.WriteLineAsync($"E seed-dev: the dev ingest token was refused ({minted.Error!.Code}): {minted.Error.Message}");

            return MigratorExitCodes.MigrationFailed;
        }

        var token = minted.Value.Token;
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"seed-dev: ingest token {token.Name} minted for project dev-project (scopes: {string.Join(", ", token.Scopes)})"));

        // The secret is recorded before it is written: a failure while printing it must still be able to redact it.
        mintedSecret.Value = minted.Value.Secret;

        // The only place the secret is ever written. Everything after this line must stay secret-free.
        await output.WriteLineAsync("EVIDGATE_DEV_TOKEN=" + minted.Value.Secret).ConfigureAwait(false);
        await output.WriteLineAsync(ShownOnceNote).ConfigureAwait(false);

        return MigratorExitCodes.Success;
    }

    /// <summary>
    /// The live token's prefix, or null when the dev project has none: the lookup runs inside a read-only tenant scope,
    /// so row level security — not a hand-written org predicate — decides what is visible.
    /// </summary>
    private static async Task<string?> FindLiveTokenAsync(IServiceProvider provider)
    {
        const string sql =
            """
            select t.prefix
              from public.ingest_token t
             where t.project_id = @project_id
               and t.name = @name
               and t.revoked_at is null
               and (t.expires_at is null or t.expires_at > now())
               and (t.overlap_ends_at is null or t.overlap_ends_at > now())
             order by t.created_at
             limit 1
            """;

        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var db = provider.GetRequiredService<ITenantDb>();

        await using var scope = await scopes
            .BeginAsync(DevSeed.OrgId, DevSeed.ProjectId, readOnlyTransaction: true)
            .ConfigureAwait(false);

        return await db
            .ScalarAsync<string>(sql, new { project_id = DevSeed.ProjectId, name = DevSeed.TokenName })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="message"/> with every credential this path holds masked: both connection strings' passwords, the
    /// pepper and the minted secret. A driver or writer message can quote the value it was given.
    /// </summary>
    private static string Redacted(
        string message,
        MigratorOptions options,
        IReadOnlyDictionary<string, string?>? environment,
        string? mintedSecret)
    {
        var values = environment ?? MigratorOptions.ProcessEnvironment();
        var app = values.TryGetValue(SecretNames.DatabaseConnectionString, out var connectionString) ? connectionString : null;
        var pepper = values.TryGetValue(SecretNames.CiPepper, out var configuredPepper) ? configuredPepper : null;

        var redacted = SecretRedactor.Redact(message, SecretRedactor.PasswordOf(options.ConnectionString));
        redacted = SecretRedactor.Redact(redacted, SecretRedactor.PasswordOf(app ?? string.Empty));
        redacted = SecretRedactor.Redact(redacted, pepper);

        return SecretRedactor.Redact(redacted, mintedSecret);
    }

    /// <summary>Carries the minted secret back to the failure handler, so a message quoting it can be redacted.</summary>
    private sealed class MintedSecret
    {
        internal string? Value { get; set; }
    }

    /// <summary>The repository's <c>contracts/</c> directory, or null when it does not sit next to <c>migrations/</c>.</summary>
    private static string? ContractsDirectory(string migrationsPath)
    {
        var root = Path.GetDirectoryName(migrationsPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var contracts = Path.Combine(root, "contracts");

        return Directory.Exists(Path.Combine(contracts, "schemas")) ? contracts : null;
    }

    /// <summary>
    /// A session-level PostgreSQL advisory lock on a dedicated app-role connection: the lookup and the mint are one
    /// critical section, so a second concurrent first run blocks, then sees the token and mints nothing. Advisory locks
    /// are per database and released with the session, so a crashed run leaves no stale lock behind.
    /// </summary>
    /// <remarks>
    /// Residual risk: the lock only orders runs of this command. A revoke that races the check-to-mint window, or any
    /// writer outside this path, can still leave a second live <c>dev-seed</c> token — the table has no unique key on
    /// <c>(project_id, name)</c> — and a reset (<c>scripts/dev/down.sh &amp;&amp; scripts/dev/up.sh --reset</c>) is the
    /// way back.
    /// </remarks>
    internal sealed class SeedGuard : IAsyncDisposable
    {
        private static readonly long Key = BinaryPrimitives.ReadInt64BigEndian(
            SHA256.HashData("evidgate.dev-seed.ingest-token"u8));

        private readonly NpgsqlConnection _connection;

        private SeedGuard(NpgsqlConnection connection) => _connection = connection;

        internal static async Task<SeedGuard> AcquireAsync(NpgsqlDataSource dataSource)
        {
            var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
            try
            {
                await LockAsync(connection, "select pg_advisory_lock(@key)").ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new SeedGuard(connection);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Released before the connection returns to the pool: a pooled session outlives the run, and its lock
                // would otherwise outlive the process that took it.
                await LockAsync(_connection, "select pg_advisory_unlock(@key)").ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGone(exception))
            {
                // A broken or already-closed session has lost the lock, and the secret may be printed by now: this must
                // not change the exit code of a successful run.
            }
            finally
            {
                try
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (IsGone(exception))
                {
                    // Closing a session that is already gone has nothing left to fail; the process is exiting.
                }
            }
        }

        /// <summary>
        /// The failures of touching a session that is no longer usable: the transport broke, or the connection was
        /// already closed or disposed (which Npgsql reports through <see cref="InvalidOperationException"/>) before the
        /// command reaches the server.
        /// </summary>
        private static bool IsGone(Exception exception) =>
            exception is NpgsqlException or InvalidOperationException;

        private static async Task LockAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("key", Key);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
