using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The two gzip-decode caps of 04.1.1-T3 (00 §9.3/§9.4, DECISIONS §8.1 D-A8), bound from
/// <c>EVIDGATE:Ingestion</c> and validated at boot like every other <c>EVIDGATE__*</c> group
/// (01.1.2-T1). They bound a <c>Content-Encoding: gzip</c> body after the 1 MiB transport cap has
/// already been applied to the bytes as sent: the decoded size may never pass
/// <see cref="MaxExpandedBytes"/>, and it may never pass <see cref="MaxExpansionRatio"/> times the
/// compressed size — so a small compressed body can never expand into an unbounded buffer (the
/// 50 MiB-from-20 KiB bomb of AC7).
/// <para>
/// The 1 MiB raw cap and the 500-event cap stay code constants (<see cref="Endpoints.IngestionEndpoints.MaxEventBatchBytes"/>,
/// <c>EventBatchParser.MaxEvents</c>): they are the published contract numbers, not deployment knobs.
/// </para>
/// </summary>
internal sealed class IngestionLimits
{
    /// <summary>Configuration section bound to <c>EVIDGATE__INGESTION__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Ingestion";

    /// <summary>
    /// Hard cap on the bytes a gzip body may expand to (default 8 MiB, DECISIONS §8.1 D-A8). The
    /// decode aborts the moment the expanded size passes it, before the JSON parser ever sees the body.
    /// </summary>
    [Range(1_048_576, 67_108_864, ErrorMessage = "EVIDGATE__INGESTION__MAXEXPANDEDBYTES must be between 1048576 and 67108864")]
    public int MaxExpandedBytes { get; init; } = 8_388_608;

    /// <summary>
    /// Maximum expansion ratio (default 20:1, DECISIONS §8.1 D-A8): a gzip body whose decoded size
    /// passes <c>ratio × compressed size</c> is refused even when it is still under
    /// <see cref="MaxExpandedBytes"/>, so a tiny compressed body cannot buy a large parse budget.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "EVIDGATE__INGESTION__MAXEXPANSIONRATIO must be between 1 and 1000")]
    public int MaxExpansionRatio { get; init; } = 20;
}
