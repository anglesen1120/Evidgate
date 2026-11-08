using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// Builds the <see cref="ContractSchemaSet"/> singleton in the background once the host starts, so the first
/// body-validating request never pays the Roslyn compile of every schema (about 55 s for the first load of a
/// process) inside an open database transaction.
/// <para>
/// It resolves the singleton through the container instead of calling <see cref="ContractSchemaSet.Load"/>:
/// the lazy registration stays the only place that locates the contracts directory, and a request that
/// arrives while the warm-up runs waits on the container's singleton lock instead of compiling a second
/// copy. A failure is logged and kept in <see cref="Completion"/>; nothing is cached, so the request path
/// fails with the same exception it always did.
/// </para>
/// </summary>
public sealed partial class ContractSchemaWarmUpService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ContractSchemaWarmUpService> _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates the service over the container that owns the schema set.</summary>
    public ContractSchemaWarmUpService(IServiceProvider services, ILogger<ContractSchemaWarmUpService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _logger = logger;
    }

    /// <summary>Completes when the schema set is built; faults with the load exception when it is not.</summary>
#pragma warning disable VSTHRD003 // The source is this service's own completion source, not a task started elsewhere.
    public Task Completion => _completion.Task;
#pragma warning restore VSTHRD003

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Task.Run keeps the compile off the startup thread: the host must start serving its health
        // endpoints while the compiler runs.
        _ = Task.Run(WarmUp, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void WarmUp()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _ = _services.GetRequiredService<ContractSchemaSet>();
        }
        catch (Exception exception)
        {
            LogFailed(exception);
            _completion.SetException(exception);
            return;
        }

        _completion.SetResult();
        var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogReady(elapsedMs);
    }

    [LoggerMessage(
        EventId = 1220,
        Level = LogLevel.Information,
        Message = "Contract schema set compiled at startup in {ElapsedMs} ms")]
    private partial void LogReady(long elapsedMs);

    [LoggerMessage(
        EventId = 1221,
        Level = LogLevel.Error,
        Message = "Contract schema set failed to build at startup; body-validating requests will fail until it does")]
    private partial void LogFailed(Exception exception);
}
