using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// C2 stage 1 (00 §5.5 C2, telemetry → …): one span plus a duration measurement
/// per dispatched message, tagged by message type and <c>error.type</c> on a
/// thrown failure (01.4.2 §11 — 10's dashboards chart these). Runs for every
/// message — the tenant-bound stages further in are the ones restricted to the
/// C8 Result contract — and carries no tenant id or payload (00 §5.7 S3).
/// </summary>
public sealed partial class TelemetryBehaviour<TMessage, TResponse>(
    ILogger<TelemetryBehaviour<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var operation = typeof(TMessage).Name;
        using var activity = EvidgateTelemetry.StartPipelineActivity(operation);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await next(message, cancellationToken).ConfigureAwait(false);
            EvidgateTelemetry.RecordPipelineDuration(operation, Stopwatch.GetElapsedTime(started), error: false);
            return response;
        }
        catch (Exception exception)
        {
            EvidgateTelemetry.RecordPipelineDuration(operation, Stopwatch.GetElapsedTime(started), error: true);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag(EvidgateTelemetry.PipelineErrorTag, exception.GetType().FullName);
            LogHandlerFailed(exception, operation, exception.GetType().Name);
            throw;
        }
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "Pipeline handler {Operation} failed with {ErrorType}")]
    private partial void LogHandlerFailed(Exception exception, string operation, string errorType);
}
