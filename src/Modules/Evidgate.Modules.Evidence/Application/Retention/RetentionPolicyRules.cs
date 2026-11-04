using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Evidence.Application.Retention;

/// <summary>
/// The refusals of the retention routes. The range refusal carries the documented code and names both the offending
/// member and the configured bound it missed, so a caller can correct the request without reading the deployment's
/// configuration; the message never echoes the submitted value.
/// </summary>
internal static class RetentionPolicyErrors
{
    /// <summary>422: the value is outside the deployment's configured range for that window.</summary>
    /// <param name="field">The request member that failed, without a leading slash.</param>
    /// <param name="reason">The machine reason, e.g. <c>below_min_artifact_days</c>.</param>
    /// <param name="boundName">The configuration member that supplied the bound.</param>
    /// <param name="bound">The bound's value.</param>
    /// <param name="message">The human explanation, naming the bound.</param>
    internal static Error OutOfRange(string field, string reason, string boundName, int bound, string message) =>
        Error.Unprocessable(
            ApiErrorCode.RetentionOutOfRange,
            message,
            [
                new ErrorDetail(field, reason),
                new ErrorDetail(boundName, bound.ToString(CultureInfo.InvariantCulture)),
            ]);

    /// <summary>
    /// 404: the project is unknown, soft-deleted or belongs to another tenant. Row level security makes another
    /// tenant's project invisible, so the three cases are one answer and never an oracle.
    /// </summary>
    internal static Error ProjectNotFound => Error.NotFound("the project does not exist in this tenant");
}

/// <summary>
/// The one place a requested window is judged against <see cref="RetentionLimits"/>, shared by the command handler and
/// its tests so the wire answer cannot drift from the rule. It runs before any statement, so a refused request writes
/// nothing. The order is fixed: the artifact window first, then the metadata window, then their relationship, so a
/// request with several faults always reports the same one.
/// </summary>
internal static class RetentionPolicyGate
{
    /// <summary>Null when the pair is acceptable, else the 422 <c>retention_out_of_range</c> the route answers.</summary>
    /// <param name="artifactDays">The requested artifact window.</param>
    /// <param name="metadataDays">The requested metadata window.</param>
    /// <param name="limits">The deployment's configured range.</param>
    internal static Error? Check(int artifactDays, int metadataDays, RetentionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (artifactDays < limits.MinArtifactDays)
        {
            return Below("artifact_days", "below_min_artifact_days", "retention_limits.min_artifact_days", limits.MinArtifactDays);
        }

        if (artifactDays > limits.MaxArtifactDays)
        {
            return Above("artifact_days", "above_max_artifact_days", "retention_limits.max_artifact_days", limits.MaxArtifactDays);
        }

        if (metadataDays < limits.MinMetadataDays)
        {
            return Below("metadata_days", "below_min_metadata_days", "retention_limits.min_metadata_days", limits.MinMetadataDays);
        }

        if (metadataDays > limits.MaxMetadataDays)
        {
            return Above("metadata_days", "above_max_metadata_days", "retention_limits.max_metadata_days", limits.MaxMetadataDays);
        }

        // A metadata window shorter than the artifact window would delete the record of an artifact it still holds.
        return metadataDays < artifactDays
            ? RetentionPolicyErrors.OutOfRange(
                "metadata_days",
                "below_artifact_days",
                "retention_limits.min_metadata_days",
                limits.MinMetadataDays,
                "metadata_days must not be shorter than artifact_days")
            : null;
    }

    private static Error Below(string field, string reason, string boundName, int bound) =>
        RetentionPolicyErrors.OutOfRange(
            field,
            reason,
            boundName,
            bound,
            string.Create(CultureInfo.InvariantCulture, $"{field} must be at least {bound}"));

    private static Error Above(string field, string reason, string boundName, int bound) =>
        RetentionPolicyErrors.OutOfRange(
            field,
            reason,
            boundName,
            bound,
            string.Create(CultureInfo.InvariantCulture, $"{field} must be at most {bound}"));
}
