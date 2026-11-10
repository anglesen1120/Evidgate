using Dapper;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The positional projection of <see cref="TaskQueueSql.QueueStateSnapshot"/>: quoted aliases keep the
/// names independent of the process-wide underscore switch.
/// </summary>
internal sealed record TaskQueueSnapshot(double OldestAgeSeconds, long DeadLetters);
