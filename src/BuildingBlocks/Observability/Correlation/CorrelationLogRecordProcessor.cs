using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Evidgate.BuildingBlocks.Observability.Correlation;

/// <summary>
/// Puts the correlation ids of the current <see cref="Activity"/> on every exported
/// log record (10.3.1-T1.2, NFR-10-1): a log line is searchable by
/// <c>request_id</c>/<c>run_id</c>/<c>job_id</c>/<c>trace_id</c> even when the logging
/// call site knows nothing about them, which is what lets an operator follow one
/// <c>trace_id</c> from the request through the outbox to a dead letter.
/// </summary>
/// <remarks>
/// <para>
/// The processor is registered by <c>AddEvidgateTelemetry</c> as the <em>first</em> processor
/// of the pipeline it builds — ahead of <c>LogRedactionProcessor</c> and of the exporter —
/// because processors run in registration order and an export processor buffers the record at
/// its own <c>OnEnd</c>: anything added after the exporter never reaches the backend. It is
/// never registered through a second <c>AddOpenTelemetry</c> on the logging builder: that call
/// registers a second <c>ILoggerProvider</c> and every record would be exported twice.
/// </para>
/// <para>
/// It only ever adds a field that is absent: an explicit attribute of the same name on
/// the record (a module that tagged its own value) wins, and a record emitted outside
/// any activity is left exactly as it was.
/// </para>
/// </remarks>
public sealed class CorrelationLogRecordProcessor : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        // A fresh list per record: the SDK requires a processor callback to be
        // thread-safe and to keep its own state local.
        var attributes = data.Attributes is { } existing
            ? new List<KeyValuePair<string, object?>>(existing)
            : [];
        var changed = false;

        changed |= AddIfAbsent(
            attributes,
            CorrelationNames.RequestId,
            Value(activity, CorrelationNames.RequestId, CorrelationNames.RequestIdBaggage, CorrelationNames.SafeRequestId));
        changed |= AddIfAbsent(
            attributes,
            CorrelationNames.RunId,
            Value(activity, CorrelationNames.RunId, CorrelationNames.RunIdBaggage, CorrelationNames.SafeRunId));
        changed |= AddIfAbsent(
            attributes,
            CorrelationNames.JobId,
            CorrelationNames.SafeJobId(CorrelationNames.CorrelationText(activity.GetTagItem(CorrelationNames.JobId))));

        if (activity.TraceId != default)
        {
            changed |= AddIfAbsent(attributes, CorrelationNames.TraceId, activity.TraceId.ToHexString());
        }

        if (changed)
        {
            data.Attributes = attributes;
        }
    }

    /// <summary>
    /// The span tag of <paramref name="attribute"/>, falling back to the baggage item of the same
    /// meaning — both passed through <paramref name="accept"/>, because a tag and a baggage item
    /// are input and a value that is not an id must not become a log attribute. The tag is read as
    /// correlation text so a <see cref="Guid"/> tag (the modules' run id) is not silently lost.
    /// </summary>
    private static string? Value(Activity activity, string attribute, string baggage, Func<string?, string?> accept) =>
        accept(
            CorrelationNames.CorrelationText(activity.GetTagItem(attribute))
            ?? activity.GetBaggageItem(baggage));

    /// <summary>Adds one pair unless the record already carries that key; returns whether the list changed.</summary>
    private static bool AddIfAbsent(List<KeyValuePair<string, object?>> attributes, string key, object? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var pair in attributes)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                return false;
            }
        }

        attributes.Add(new KeyValuePair<string, object?>(key, value));
        return true;
    }
}
