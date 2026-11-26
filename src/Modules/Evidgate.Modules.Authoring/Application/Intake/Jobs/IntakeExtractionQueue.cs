using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;

namespace Evidgate.Modules.Authoring.Application.Intake.Jobs;

/// <summary>The task type and the one enqueue of the extraction job.</summary>
internal static class IntakeExtractionQueue
{
    /// <summary>The <c>task_type</c> the extraction handler consumes.</summary>
    internal const string TaskType = "authoring_intake_extract";

    private const string AggregateType = "authoring_intake";

    /// <summary>
    /// Enqueues the extraction of one received intake in the caller's open tenant transaction, so the row and its
    /// job commit together or not at all. The key is per intake: a replayed create enqueues nothing twice.
    /// </summary>
    /// <param name="outbox">The outbox writer bound to the caller's transaction.</param>
    /// <param name="intakeId">The received intake.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outbox row id.</returns>
    internal static Task<Guid> EnqueueAsync(IOutboxWriter outbox, Guid intakeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        return outbox.EnqueueAsync(
            TaskType,
            JsonSerializer.Serialize(new { intake_id = intakeId }),
            AggregateType,
            intakeId,
            $"authoring:intake:{intakeId:D}:extract",
            cancellationToken);
    }
}
