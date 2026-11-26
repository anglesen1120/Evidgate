using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Authoring.Application.Intake.Jobs;

namespace Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;

/// <summary>
/// The queue's entry into <see cref="ExtractIntakeJob"/>. It opens its own transactions because the job reads an object
/// and parses a document between its two writes, which no consumer-held transaction may span.
/// </summary>
internal sealed class ExtractIntakeTaskHandler(ExtractIntakeJob job) : ITransactionlessTaskHandler<ExtractIntakePayload>
{
    /// <inheritdoc />
    public string TaskType => IntakeExtractionQueue.TaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(ExtractIntakePayload payload, Guid orgId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await job.RunAsync(payload.IntakeId, orgId, cancellationToken).ConfigureAwait(false);
    }
}
