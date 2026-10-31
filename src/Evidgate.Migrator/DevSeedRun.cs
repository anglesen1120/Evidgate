using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// The prologue every <c>seed-dev</c> part shares: compose the modules over the app role, refuse a non-local
/// application host, take the seed's advisory lock, write the tenant as the owner role, then hand the caller the
/// mediator and the container under the synthetic dev principal.
/// </summary>
/// <remarks>
/// One copy, so the plain seed and <c>--with-token</c> cannot drift apart in their refusals or in the order of the
/// writes. The tenant rows stay on the owner role; anything below runs on the app role, inside a tenant scope and under
/// the seeded dev user, exactly as a request would.
/// </remarks>
internal static class DevSeedRun
{
    /// <summary>Runs the catalog seed and then <paramref name="commands"/> after the refusals and the tenant write; returns the process exit code.</summary>
    /// <param name="options">Owner-role connection and paths.</param>
    /// <param name="environment">Environment snapshot; null reads the process environment.</param>
    /// <param name="contractsDirectory">The repository's <c>contracts/</c> directory.</param>
    /// <param name="preflight">An extra refusal that needs the composed container, or null; its text is printed and nothing is written.</param>
    /// <param name="commands">What else to do inside the tenant scope and the synthetic principal, or null for nothing.</param>
    /// <param name="loggerProvider">An extra log sink, for tests that assert what reaches a logger.</param>
    /// <param name="output">Where progress goes.</param>
    /// <param name="error">Where a refusal or failure goes.</param>
    /// <remarks>
    /// The catalog sample and the environment revision are part of every seed, not of one switch: both modes run here,
    /// and a stack seeded only by <c>--with-token</c> would otherwise have no suite revision or environment revision for
    /// a manifest and a run to name.
    /// </remarks>
    internal static async Task<int> RunModulesAsync(
        MigratorOptions options,
        IReadOnlyDictionary<string, string?>? environment,
        string contractsDirectory,
        Func<IServiceProvider, Task<string?>>? preflight,
        Func<IMediator, IServiceProvider, Task<int>>? commands,
        ILoggerProvider? loggerProvider,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        await using var provider = DevSeedServices.Compose(environment, contractsDirectory, loggerProvider);

        // Resolving the data source validates the application connection string and the evidgate_app role pin, so a
        // missing or wrong-role app connection fails closed here — before the owner-role tenant write below.
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        var appHost = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host;
        if (!DevSeed.IsLocalHost(appHost))
        {
            await error.WriteLineAsync($"E seed-dev refused: the application database host '{appHost}' is not local or a compose service name; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }

        // The lock comes first so two first runs cannot race the tenant insert or the rows the commands write.
        await using var guard = await DevSeedToken.SeedGuard.AcquireAsync(dataSource).ConfigureAwait(false);

        if (preflight is not null && await preflight(provider).ConfigureAwait(false) is { } refusal)
        {
            await error.WriteLineAsync(refusal).ConfigureAwait(false);

            return MigratorExitCodes.InvalidInvocation;
        }

        // The dev user's membership is what the permission evaluator reads, so the tenant is always written first —
        // idempotently, exactly as the plain seed does.
        var seeded = DevSeed.WriteTenant(options, output, error);
        if (seeded != MigratorExitCodes.Success)
        {
            return seeded;
        }

        // The synthetic caller exists only here: inside the guard, after the refusals, for the commands below.
        using var tenantContext = provider.GetRequiredService<IRequestTenantContextSetter>()
            .Publish(DevSeed.OrgId, DevSeed.ProjectId);
        using var caller = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Member(DevSeed.UserId, DevSeed.UserEmail));

        // Handlers and pipeline behaviours are scoped (as in the hosts), so the mediator comes from a scope.
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var cataloged = await DevSeedCatalog.WriteAsync(mediator, scope.ServiceProvider, output, error).ConfigureAwait(false);
        if (cataloged != MigratorExitCodes.Success)
        {
            return cataloged;
        }

        // The environment revision is part of every seed too: a run refuses a manifest whose environment_revision_id is
        // unknown or null, and the ingest token cannot create one itself.
        var (environmentRevisionId, environmentExit) = await DevSeedEnvironment
            .WriteAsync(mediator, scope.ServiceProvider, output, error)
            .ConfigureAwait(false);
        if (environmentRevisionId is null || commands is null)
        {
            return environmentExit;
        }

        return await commands(mediator, scope.ServiceProvider).ConfigureAwait(false);
    }

    /// <summary>The repository's <c>contracts/</c> directory, or null when it does not sit next to <c>migrations/</c>.</summary>
    /// <param name="migrationsPath">The resolved migrations directory.</param>
    internal static string? ContractsDirectory(string migrationsPath)
    {
        var root = Path.GetDirectoryName(migrationsPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var contracts = Path.Combine(root, "contracts");

        return Directory.Exists(Path.Combine(contracts, "schemas")) ? contracts : null;
    }
}
