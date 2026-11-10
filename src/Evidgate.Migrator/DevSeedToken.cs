using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
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
    /// <summary>The label of the one stdout line that carries the secret; the developer exports it for the CLI.</summary>
    internal const string TokenVariable = SecretNames.DevIngestToken;

    /// <summary>The line a rerun prints instead of a secret, naming the one recovery that works without a login.</summary>
    internal static readonly string RerunNote =
        $"a live '{DevSeed.TokenName}' ingest token already exists; its secret was shown once and cannot be shown again. " +
        "To get a new one run: scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token";

    /// <summary>The one-time notice printed under the secret; the token itself is on the line above it.</summary>
    internal const string ShownOnceNote =
        "seed-dev: the token above is shown once and is not stored — capture it before this output is lost";

    /// <summary>
    /// The sample <c>policy-revision.v1</c> body: the seeded catalog suite a run must cover and the flaky budget. The
    /// suite is the catalog seed's slug, because a required suite no run can cover would make every decision INCONCLUSIVE. Deliberately
    /// minimal and fixed, so the content address is stable and a rerun replays the stored revision instead of appending.
    /// </summary>
    private const string SamplePolicyBody = """{"flaky_budget":3,"required_suites":["dev-seed-smoke"]}""";

    /// <summary>Applies the seed; <paramref name="withToken"/> adds the ingest token and the sample policy revision.</summary>
    /// <param name="options">Owner-role connection and paths (the tenant is written as the owner first).</param>
    /// <param name="environment">Environment snapshot; null reads the process environment.</param>
    /// <param name="output">Where progress goes, including the one-time secret.</param>
    /// <param name="error">Where a refusal or failure goes; never the secret.</param>
    /// <param name="loggerProvider">An extra log sink, for tests that assert the secret reaches no logger.</param>
    /// <param name="withToken">Also mint the dev ingest token and the sample policy revision; the catalog sample is written either way.</param>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The command line is synchronous end to end; the async flow runs to completion before the exit code is returned, exactly as the sync secret read does.")]
    internal static int Run(
        MigratorOptions options,
        IReadOnlyDictionary<string, string?>? environment,
        TextWriter output,
        TextWriter error,
        ILoggerProvider? loggerProvider = null,
        bool withToken = true)
    {
        var label = withToken ? "seed-dev --with-token" : "seed-dev";
        if (DevSeedRun.ContractsDirectory(options.MigrationsPath) is not { } contractsDirectory)
        {
            error.WriteLine(
                $"E {label} refused: contracts/schemas was not found next to '{options.MigrationsPath}'; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }

        // The minted secret is handed back here so a failure message that quotes it can be redacted.
        var mintedSecret = new MintedSecret();

        try
        {
            return RunAsync(environment, contractsDirectory, options, output, error, loggerProvider, mintedSecret, withToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (OptionsValidationException exception)
        {
            // Options validation names keys, never values, so its failures are safe to print as they are.
            error.WriteLine($"E {label} failed: " + string.Join("; ", exception.Failures) + "; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }
        catch (NpgsqlException exception)
        {
            error.WriteLine($"E {label} failed: " + Redacted(exception.Message, options, environment, mintedSecret.Value));

            return exception.IsTransient || exception.InnerException is System.Net.Sockets.SocketException
                ? MigratorExitCodes.DatabaseUnavailable
                : MigratorExitCodes.MigrationFailed;
        }
        catch (Exception exception)
        {
            // The seed handles a secret: a message is printed only after the redactor has run over it.
            error.WriteLine($"E {label} failed: " + Redacted(exception.Message, options, environment, mintedSecret.Value));

            return MigratorExitCodes.MigrationFailed;
        }
    }

    /// <summary>
    /// The mode-specific work, inside the shared prologue: with <paramref name="withToken"/> the pepper is refused
    /// (writing nothing) before the tenant is written, and the policy and the mint then run under the seed's
    /// advisory-lock guard, so two concurrent first runs cannot both mint.
    /// </summary>
    private static Task<int> RunAsync(
        IReadOnlyDictionary<string, string?>? environment,
        string contractsDirectory,
        MigratorOptions options,
        TextWriter output,
        TextWriter error,
        ILoggerProvider? loggerProvider,
        MintedSecret mintedSecret,
        bool withToken) =>
        DevSeedRun.RunModulesAsync(
            options,
            environment,
            contractsDirectory,
            preflight: withToken
                ? provider => Task.FromResult(PepperConfigured(provider, out var refusal) ? null : refusal)
                : null,
            commands: withToken
                ? (mediator, services) => WriteTokenAsync(mediator, services, output, error, mintedSecret)
                : null,
            loggerProvider: loggerProvider,
            output: output,
            error: error);

    /// <summary>
    /// The policy is part of every run, not of the first one: an existing token skips only the mint, so a stack whose
    /// policy revision was never seeded (or was seeded by a run that predates it) still ends up with one.
    /// </summary>
    private static async Task<int> WriteTokenAsync(
        IMediator mediator,
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        MintedSecret mintedSecret)
    {
        var policy = await WriteSamplePolicyAsync(mediator, output, error).ConfigureAwait(false);
        if (policy != MigratorExitCodes.Success)
        {
            return policy;
        }

        if (await FindLiveTokenAsync(services).ConfigureAwait(false) is not null)
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

    /// <summary>
    /// The dev project's one sample revision and its activation, both through the real commands; a rerun replays both
    /// (200) instead of appending or re-activating.
    /// </summary>
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

        // Without an active policy a run created afterwards pins no revision and its evaluation task fails forever, so
        // the seed is not done at the append. The key names the revision, so a rerun replays the stored 200 instead of
        // writing a second audit row, and new sample content (a new revision id) gets a key of its own rather than a 409.
        var activated = await mediator
            .Send(new ActivatePolicyCommand(
                DevSeed.ProjectId,
                ActivationBody(revision.Id),
                ActivationKey(DevSeed.ProjectId, revision.Id)))
            .ConfigureAwait(false);

        if (!activated.IsSuccess)
        {
            await error.WriteLineAsync($"E seed-dev: the sample policy revision could not be activated ({activated.Error!.Code}): {activated.Error.Message}");

            return MigratorExitCodes.MigrationFailed;
        }

        var activation = activated.Value;
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"seed-dev: sample policy revision {revision.RevisionNo} {(activation.Changed ? "activated" : "already active")} ({activation.Mode})"));

        return MigratorExitCodes.Success;
    }

    /// <summary>The activation body: the sample revision, in the only mode this milestone accepts.</summary>
    private static byte[] ActivationBody(Guid revisionId) => Encoding.UTF8.GetBytes(string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"policy_revision_id":"{{revisionId:D}}","mode":"advisory"}"""));

    /// <summary>
    /// The activation's <c>Idempotency-Key</c>, derived from the two ids so a rerun replays the stored answer instead of
    /// re-activating; naming the revision keeps new sample content from conflicting with the previous key.
    /// </summary>
    private static string ActivationKey(Guid projectId, Guid revisionId) => string.Create(
        CultureInfo.InvariantCulture,
        $"dev-seed-policy-activate:{projectId:N}:{revisionId:N}");

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
        await output.WriteLineAsync(TokenVariable + "=" + minted.Value.Secret).ConfigureAwait(false);
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

    /// <summary>
    /// A session-level PostgreSQL advisory lock on a dedicated app-role connection: one seed at a time, so the tenant write,
    /// the catalog rows and the token mint are one critical section and a second concurrent first run blocks, then finds
    /// everything already there. Advisory locks are per database and released with the session, so a crashed run leaves
    /// no stale lock behind.
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

        internal static async Task<SeedGuard> AcquireAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // No command timeout on the wait: a run that arrives second must wait for the holder's session rather
                // than fail after Npgsql's 30 s default. The session owns the lock, so the wait can only end with the
                // holder's run or the caller's cancellation.
                await LockAsync(connection, "select pg_advisory_lock(@key)", cancellationToken, timeoutSeconds: 0)
                    .ConfigureAwait(false);
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
                // would otherwise outlive the process that took it. The unlock never waits on another session, so it
                // keeps the connection's command timeout rather than the lock command's unbounded one.
                await LockAsync(_connection, "select pg_advisory_unlock(@key)", CancellationToken.None)
                    .ConfigureAwait(false);
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

        private static async Task LockAsync(
            NpgsqlConnection connection,
            string sql,
            CancellationToken cancellationToken,
            int? timeoutSeconds = null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (timeoutSeconds is { } seconds)
            {
                command.CommandTimeout = seconds;
            }

            command.Parameters.AddWithValue("key", Key);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
