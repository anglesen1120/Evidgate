using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The fixed-window admission limits of <c>POST /v1/runs/{r}/events</c>, bound from
/// <c>EVIDGATE:Ingestion:RateLimit</c>. They bound requests per authenticated token id
/// and per project, so a burst answers 429 <c>rate_limited</c> before the body is read.
/// <para>
/// The window state is per process: each replica enforces its own budget, so the
/// platform-wide burst a project may send is <c>ProjectPermitLimit × replicas</c> per window.
/// A shared counter store is not wired yet; that gap is recorded in the feature plan.
/// </para>
/// </summary>
internal sealed class IngestionRateLimits : IValidatableObject
{
    /// <summary>Configuration section bound to <c>EVIDGATE__INGESTION__RATELIMIT__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Ingestion:RateLimit";

    /// <summary>Accepted batch requests of one token inside one window (default 120).</summary>
    [Range(1, 100_000, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__PERMITLIMIT must be between 1 and 100000")]
    public int PermitLimit { get; init; } = 120;

    /// <summary>
    /// Accepted batch requests of one project inside one window, every token included
    /// (default 480 = 4 × <see cref="PermitLimit"/>, never below it). It stays above the
    /// token budget so one token is capped below the project's and its siblings keep a share.
    /// </summary>
    [Range(1, 400_000, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__PROJECTPERMITLIMIT must be between 1 and 400000")]
    public int ProjectPermitLimit { get; init; } = 480;

    /// <summary>Length of the fixed window in seconds (default 60); it is also the ceiling of every <c>Retry-After</c>.</summary>
    [Range(1, 3600, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__WINDOWSECONDS must be between 1 and 3600")]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Resident partitions before ended windows are swept (default 10,000). A request needs
    /// two: its project's and its token's, so a smaller map would refuse every request.
    /// A token that finds no room is charged to the project's budget instead of growing the map.
    /// </summary>
    [Range(2, 100_000, ErrorMessage = "EVIDGATE__INGESTION__RATELIMIT__MAXPARTITIONS must be between 2 and 100000")]
    public int MaxPartitions { get; init; } = 10_000;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProjectPermitLimit < PermitLimit)
        {
            yield return new ValidationResult(
                "EVIDGATE__INGESTION__RATELIMIT__PROJECTPERMITLIMIT must be at least EVIDGATE__INGESTION__RATELIMIT__PERMITLIMIT",
                [nameof(ProjectPermitLimit)]);
        }
    }
}
