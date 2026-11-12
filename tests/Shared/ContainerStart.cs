using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Evidgate.Testing;

/// <summary>
/// Bounds of <see cref="ContainerStart"/>. One Docker API call (create, start) is cut off by the Docker
/// client after 100 s; on a loaded host a single stalled call used to fail every test of the class whose
/// fixture started the container, so a start gets a few bounded attempts inside one wall-clock budget.
/// </summary>
/// <param name="MaxAttempts">Ceiling on the number of create+start attempts.</param>
/// <param name="AttemptTimeout">Ceiling of one attempt, wait strategy included.</param>
/// <param name="Budget">Wall-clock ceiling for all attempts together.</param>
/// <param name="Delay">Pause between two attempts.</param>
/// <param name="DisposeTimeout">Ceiling for disposing the container of a failed attempt.</param>
public sealed record ContainerStartOptions(
    int MaxAttempts,
    TimeSpan AttemptTimeout,
    TimeSpan Budget,
    TimeSpan Delay,
    TimeSpan DisposeTimeout)
{
    /// <summary>Three attempts, none past three minutes, all inside five (a fixture token shorter than that still wins).</summary>
    public static ContainerStartOptions Default { get; } = new(
        MaxAttempts: 3,
        AttemptTimeout: TimeSpan.FromMinutes(3),
        Budget: TimeSpan.FromMinutes(5),
        Delay: TimeSpan.FromSeconds(2),
        DisposeTimeout: TimeSpan.FromSeconds(30));
}

/// <summary>
/// The one container start every fixture uses: it builds a fresh container per attempt, retries only the
/// Docker API failures a loaded host produces (a timed-out or reset call, a 5xx answer), disposes the
/// half-created container before the next attempt and logs each retry with the failing call. An image that
/// does not exist, a 4xx answer, a wait-strategy timeout or a caller's cancellation is a real fault and
/// surfaces at once.
/// </summary>
public static class ContainerStart
{
    /// <summary>Builds, starts and returns a container; on the final failure nothing is left running.</summary>
    public static Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> create,
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
        where TContainer : IContainer =>
        StartWithReaperAsync(create, options, log, cancellationToken);

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
        await ReaperLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureResourceReaperAsync(
                name,
                () => ResourceReaper.IsUnavailable,
                token => ResourceReaper.GetAndStartDefaultAsync(auth, NullLogger.Instance, false, token),
                async token =>
                {
                    var builder = auth.GetDockerClientBuilder(ResourceReaper.DefaultSessionId);
                    using var client = builder.Build();
                    try
                    {
                        await client.Containers
                            .RemoveContainerAsync(name, new ContainerRemoveParameters { Force = true }, token)
                            .ConfigureAwait(false);
                    }
                    catch (DockerContainerNotFoundException)
                    {
                        // Gone already, or dockerd has not finished creating it: the next attempt tells.
                    }
                },
                options,
                log,
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
        Func<CancellationToken, Task> removeOwnContainer,
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownContainerName);
        options ??= ContainerStartOptions.Default;
        log ??= static message => Console.Error.WriteLine(message);
        var deadline = DateTime.UtcNow + options.Budget;
        for (var attempt = 1; isUnavailable(); attempt++)
        {
            var remaining = deadline - DateTime.UtcNow;
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
                await RemoveQuietlyAsync(removeOwnContainer, deadline, options, log).ConfigureAwait(false);
            }

            await Task.Delay(options.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

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
        Action<string> log)
    {
        var budget = deadline - DateTime.UtcNow;
        var timeout = budget < options.DisposeTimeout ? budget : options.DisposeTimeout;
        try
        {
            using var cts = new CancellationTokenSource(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
            await remove(cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log($"[container-start] removing the stale reaper container failed: {exception.GetType().Name}");
        }
    }

    /// <summary>The delegate form of <see cref="StartAsync{TContainer}"/>, so the loop is testable without Docker.</summary>
    public static async Task<T> StartAsync<T>(
        Func<T> create,
        Func<T, CancellationToken, Task> start,
        Func<T, ValueTask> dispose,
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(dispose);
        options ??= ContainerStartOptions.Default;
        log ??= static message => Console.Error.WriteLine(message);

        var deadline = DateTime.UtcNow + options.Budget;
        ExceptionDispatchInfo? failure = null;
        for (var attempt = 1; ; attempt++)
        {
            // A delay that overshoots the deadline must surface the Docker failure, not a negative timeout.
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                failure?.Throw();
            }

            using var attemptTimeout = new CancellationTokenSource(
                remaining < options.AttemptTimeout ? remaining : options.AttemptTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);

            var container = create();
            try
            {
                await start(container, linked.Token).ConfigureAwait(false);
                return container;
            }
            catch (Exception exception)
            {
                var cleanupBudget = deadline - DateTime.UtcNow;
                var cleanupTimeout = cleanupBudget < options.DisposeTimeout ? cleanupBudget : options.DisposeTimeout;
                await DisposeQuietlyAsync(
                    container,
                    dispose,
                    cleanupTimeout > TimeSpan.Zero ? cleanupTimeout : TimeSpan.Zero,
                    log).ConfigureAwait(false);

                var last = attempt >= options.MaxAttempts || deadline - DateTime.UtcNow <= options.Delay;
                if (last || cancellationToken.IsCancellationRequested || !IsTransient(exception))
                {
                    throw;
                }

                log(
                    $"[container-start] attempt {attempt}/{options.MaxAttempts} failed in "
                    + $"{FailingCall(exception)}: {exception.GetType().Name}: {exception.Message}; retrying in "
                    + $"{options.Delay.TotalSeconds:0.#}s");
                failure = ExceptionDispatchInfo.Capture(exception);
            }

            await Task.Delay(options.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True for a failure of the Docker API itself under load: a call cut off by its timeout, a reset or
    /// refused connection, a 408/5xx answer. A 4xx answer (missing image, bad config) is never transient,
    /// and a fatal link anywhere in the chain wins over a transient one.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var transient = false;
        foreach (var link in Chain(exception))
        {
            switch (link)
            {
                case DockerApiException api when IsClientError(api.StatusCode):
                    return false;
                case DockerApiException api when (int)api.StatusCode >= 500 || api.StatusCode == HttpStatusCode.RequestTimeout:
                    transient = true;
                    break;
                case FileNotFoundException or DirectoryNotFoundException:
                    return false;
                case OperationCanceledException or HttpRequestException or IOException or SocketException:
                    transient = true;
                    break;
            }
        }

        return transient;
    }

    private static bool IsClientError(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout;

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> inner = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is null ? Array.Empty<Exception>() : [exception.InnerException];
        foreach (var link in inner.SelectMany(Chain))
        {
            yield return link;
        }
    }

    /// <summary>Names the Docker call from the stack: create and start are separate API calls.</summary>
    private static string FailingCall(Exception exception)
    {
        var text = exception.ToString();
        if (text.Contains("CreateContainerAsync", StringComparison.Ordinal))
        {
            return "create";
        }

        if (text.Contains("StartContainerAsync", StringComparison.Ordinal))
        {
            return "start";
        }

        return text.Contains("DockerClient", StringComparison.Ordinal) ? "docker-api" : "start";
    }

    private static async Task DisposeQuietlyAsync<T>(
        T container,
        Func<T, ValueTask> dispose,
        TimeSpan timeout,
        Action<string> log)
    {
        try
        {
            await dispose(container).AsTask().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A stalled Docker also stalls the cleanup; the resource reaper removes what is left.
            log($"[container-start] disposing the failed container did not finish: {exception.GetType().Name}");
        }
    }
}
