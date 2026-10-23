using System.ComponentModel.DataAnnotations;
using System.Net;

namespace Evidgate.Worker.Options;

/// <summary>
/// Worker options, bound from <c>EVIDGATE__WORKER__*</c> (01.1.2-T1.1). The
/// health listener is the Worker-side readiness surface (T1.3); the poll
/// interval drives the consumer's wait-for-ready loop (T3.3).
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__WORKER__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Worker";

    /// <summary>Port of the minimal <c>/internal/health/*</c> listener.</summary>
    [Range(1, 65535, ErrorMessage = "EVIDGATE__WORKER__HEALTHPORT must be a TCP port (1..65535)")]
    public int HealthPort { get; init; } = 8081;

    /// <summary>
    /// IP address the health listener binds. Defaults to every interface so a
    /// container orchestrator can probe it; the dev stack sets <c>127.0.0.1</c>
    /// so nothing is reachable off-host (00 §5.7).
    /// </summary>
    [Required(ErrorMessage = "EVIDGATE__WORKER__HEALTHADDRESS must be an IP address")]
    [CustomValidation(typeof(WorkerOptions), nameof(ValidateHealthAddress))]
    public string HealthAddress { get; init; } = "0.0.0.0";

    /// <summary>Milliseconds between readiness polls while the consumer waits for the database.</summary>
    [Range(10, 60_000, ErrorMessage = "EVIDGATE__WORKER__READINESSPOLLMILLISECONDS must be between 10 and 60000")]
    public int ReadinessPollMilliseconds { get; init; } = 1000;

    /// <summary>Rejects a <see cref="HealthAddress"/> that is not a literal IP address.</summary>
    public static ValidationResult? ValidateHealthAddress(string? value, ValidationContext context)
        => IPAddress.TryParse(value, out _)
            ? ValidationResult.Success
            : new ValidationResult("EVIDGATE__WORKER__HEALTHADDRESS must be an IP address", [context?.MemberName ?? nameof(HealthAddress)]);
}
