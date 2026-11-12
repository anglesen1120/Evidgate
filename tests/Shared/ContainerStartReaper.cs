using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Evidgate.Testing;

/// <summary>The Ryuk reaper start of <see cref="ContainerStart"/>: recovery from a stale container under its fixed name.</summary>
public static partial class ContainerStart
{
    // Testcontainers starts its Ryuk reaper lazily inside the first container start of the process. A create
    // that times out client-side leaves a Ryuk container under the session's fixed name, so every retry of
    // that lazy start dies with 409 for the rest of the process; starting it here, with the stale one removed,
    // keeps the fixture's own container attempts independent of it.
    private static async Task<TContainer> StartWithReaperAsync<TContainer>(
        Func<TContainer> create,
        ContainerStartOptions? options,
        Action<string>? log,
        CancellationToken cancellationToken)
        where TContainer : IContainer
    {
        await EnsureResourceReaperAsync(options, log, cancellationToken).ConfigureAwait(false);
        return await StartAsync(
            create,
            (container, token) => container.StartAsync(token),
            container => container.DisposeAsync(),
            options,
            log,
            cancellationToken).ConfigureAwait(false);
    }

    private static readonly SemaphoreSlim ReaperLock = new(1, 1);

    /// <summary>Starts the process-wide Ryuk reaper once, recovering from a name conflict on its own session container.</summary>
    public static async Task EnsureResourceReaperAsync(
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!TestcontainersSettings.ResourceReaperEnabled
            || !ResourceReaper.IsUnavailable
            || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var auth = TestcontainersSettings.OS.DockerEndpointAuthConfig;
        var name = $"testcontainers-ryuk-{ResourceReaper.DefaultSessionId:D}";
        // The lock wait counts against the budget: a queued fixture must not add a second budget behind a failed one.
        options ??= ContainerStartOptions.Default;
        var deadline = DateTime.UtcNow + options.Budget;
        if (!await ReaperLock.WaitAsync(options.Budget, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException($"Waiting for another fixture to start the Ryuk reaper exceeded {options.Budget}.");
        }

        try
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Waiting for another fixture to start the Ryuk reaper exceeded {options.Budget}.");
            }

            options = options with { Budget = remaining };
            var logger = log ?? (static message => Console.Error.WriteLine(message));
            await EnsureResourceReaperAsync(
                name,
                () => ResourceReaper.IsUnavailable,
                token => WithHostLockAsync(
                    () => ResourceReaper.GetAndStartDefaultAsync(auth, NullLogger.Instance, false, token),
                    logger,
                    token),
                async (conflictingId, token) =>
                {
                    var builder = auth.GetDockerClientBuilder(ResourceReaper.DefaultSessionId);
                    using var client = builder.Build();
                    await RemoveReaperContainerAsync(client.Containers, name, conflictingId, logger, token).ConfigureAwait(false);
                },
                options,
                logger,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReaperLock.Release();
        }
    }

    /// <summary>
    /// The loop of <see cref="EnsureResourceReaperAsync(ContainerStartOptions?, Action{string}?, CancellationToken)"/>
    /// over delegates, so it is testable without Docker. A transient Docker failure or a 409 on
    /// <paramref name="ownContainerName"/> removes that container and retries inside the budget; any other
    /// failure, including a 409 on a foreign name, surfaces at once.
    /// </summary>
    public static async Task EnsureResourceReaperAsync(
        string ownContainerName,
        Func<bool> isUnavailable,
        Func<CancellationToken, Task> startReaper,
        Func<string?, CancellationToken, Task> removeOwnContainer,
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownContainerName);
        options ??= ContainerStartOptions.Default;
        log ??= static message => Console.Error.WriteLine(message);
        var deadline = DateTime.UtcNow + options.Budget;
        ExceptionDispatchInfo? failure = null;
        for (var attempt = 1; isUnavailable(); attempt++)
        {
            // Cleanup and the delay can overshoot the deadline; surface the Docker failure, not a negative timeout.
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                failure?.Throw();
            }

            using var attemptTimeout = new CancellationTokenSource(
                remaining < options.AttemptTimeout ? remaining : options.AttemptTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
            try
            {
                await startReaper(linked.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                var conflict = IsNameConflict(exception, ownContainerName);
                var last = attempt >= options.MaxAttempts || deadline - DateTime.UtcNow <= options.Delay;
                if (last || cancellationToken.IsCancellationRequested || !(conflict || IsTransient(exception)))
                {
                    throw;
                }

                log(
                    $"[container-start] reaper attempt {attempt}/{options.MaxAttempts} failed in "
                    + $"{FailingCall(exception)}: {exception.GetType().Name}: {exception.Message}; "
                    + $"removing {ownContainerName} and retrying in {options.Delay.TotalSeconds:0.#}s");
                failure = ExceptionDispatchInfo.Capture(exception);
                await RemoveQuietlyAsync(
                    ct => removeOwnContainer(ConflictingContainerId(exception, ownContainerName), ct),
                    deadline,
                    options,
                    log,
                    cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(options.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the stale reaper container by the id the 409 quoted and by its name, then waits until the name
    /// is free. Name lookup can miss a container dockerd is still registering, so the id is tried first; the
    /// wait ends with the caller's token, which makes a name that stays taken a logged removal failure.
    /// </summary>
    public static async Task RemoveReaperContainerAsync(
        IContainerOperations containers,
        string name,
        string? conflictingId,
        Action<string> log,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(log);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        // While dockerd still registers the container, both the id and the name answer 404 to a remove, so
        // every round removes again: a name that is inspectable has not been removed yet. A 409 proves the
        // name is reserved, so with a quoted id a round where no remove landed waits instead of trusting a 404.
        while (true)
        {
            var removed = false;
            foreach (var target in new[] { conflictingId, name }.Where(t => !string.IsNullOrEmpty(t)).Distinct())
            {
                try
                {
                    await containers.RemoveContainerAsync(
                        target!, new ContainerRemoveParameters { Force = true }, cancellationToken).ConfigureAwait(false);
                    log($"[container-start] removed stale reaper container by {(target == name ? "name" : "id")} {target}");
                    removed = true;
                }
                catch (DockerContainerNotFoundException)
                {
                    log($"[container-start] stale reaper container {target} not found by remove (404)");
                }
            }

            if (removed || conflictingId is null)
            {
                try
                {
                    var held = await containers.InspectContainerAsync(name, cancellationToken).ConfigureAwait(false);
                    log($"[container-start] name {name} still held by {held.ID} ({held.State?.Status}); removing again");
                }
                catch (DockerContainerNotFoundException)
                {
                    return;
                }
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    // Ten test processes of one full gate start their reaper within seconds of each other; a host-wide lock
    // spreads those creates so dockerd answers each inside the client's 100 s timeout. A lock that cannot be
    // taken in time only costs the spreading, so the start then goes ahead without it.
    private static async Task WithHostLockAsync(Func<Task> action, Action<string> log, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "evidgate-ryuk-start.lock");
        using var lockWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lockWait.CancelAfter(HostLockWait);
        FileStream? handle = null;
        try
        {
            while (handle is null)
            {
                try
                {
                    handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), lockWait.Token).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    // Another user owns the lock file: it can never be taken, so do not wait for it.
                    log("[container-start] host lock file is not accessible; starting without it");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log("[container-start] host lock for the reaper start not taken in time; starting without it");
        }

        using (handle)
        {
            await action().ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan HostLockWait = TimeSpan.FromSeconds(60);

    /// <summary>The id the daemon quotes in a 409 ("is already in use by container \"id\""), or null.</summary>
    public static string? ConflictingContainerId(Exception exception, string containerName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (var link in Chain(exception))
        {
            if (link is DockerApiException { StatusCode: HttpStatusCode.Conflict } api && NamesContainer(api.Message, containerName))
            {
                var match = ConflictId().Match(api.Message);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"in use by container \\?""([0-9a-f]{12,64})\\?""")]
    private static partial Regex ConflictId();

    /// <summary>True when a 409 anywhere in the chain names exactly the given container.</summary>
    public static bool IsNameConflict(Exception exception, string containerName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Chain(exception).Any(link =>
            link is DockerApiException { StatusCode: HttpStatusCode.Conflict } api
            && NamesContainer(api.Message, containerName));
    }

    // The daemon quotes the name ("/name", escaped inside a JSON body); a longer name sharing the prefix must not match.
    private static bool NamesContainer(string message, string containerName)
    {
        var needle = $"/{containerName}";
        for (var at = message.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = message.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            var end = at + needle.Length;
            if (end >= message.Length || !(char.IsAsciiLetterOrDigit(message[end]) || message[end] is '-' or '_' or '.'))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task RemoveQuietlyAsync(
        Func<CancellationToken, Task> remove,
        DateTime deadline,
        ContainerStartOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var budget = deadline - DateTime.UtcNow;
        var timeout = budget < options.DisposeTimeout ? budget : options.DisposeTimeout;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
            await remove(cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log($"[container-start] removing the stale reaper container failed: {exception.GetType().Name}");
        }
    }
}
