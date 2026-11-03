using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The fixed-window admission limits of <c>POST /v1/runs/{r}/events</c>, bound from
/// <c>EVIDGATE:Ingestion:RateLimit</c>. They bound requests per authenticated project
/// and token id, so a burst answers 429 <c>rate_limited</c> before the body is read.
/// <para>
/// The window state is per process: each replica enforces its own budget, so the
/// platform-wide burst a project may send is <c>PermitLimit × replicas</c> per window.
/// A shared counter store is not wired yet; that gap is recorded in the feature plan.
/// </para>
/// </summary>
internal sealed class IngestionRateLimits
{
    /// <summary>Configuration section bound to <c>EVIDGATE__INGESTION__RATELIMIT__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Ingestion:RateLimit";

    /// <summary>Accepted batch requests per project and token inside one window (default 120).</summary>
    [Range(1, 100_000, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__PERMITLIMIT must be between 1 and 100000")]
    public int PermitLimit { get; init; } = 120;

    /// <summary>Length of the fixed window in seconds (default 60); it is also the ceiling of every <c>Retry-After</c>.</summary>
    [Range(1, 3600, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__WINDOWSECONDS must be between 1 and 3600")]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Resident partitions before idle windows are swept (default 10,000). A new identity
    /// that arrives while every partition is live is refused, never admitted unbounded.
    /// </summary>
    [Range(1, 100_000, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__MAXPARTITIONS must be between 1 and 100000")]
    public int MaxPartitions { get; init; } = 10_000;
}
