using Evidgate.BuildingBlocks.Persistence.Readiness;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Evidgate.BuildingBlocks.Persistence.Health;

/// <summary>
/// The ASP.NET Core health check behind <c>GET /internal/health/ready</c>
/// (01.1.2-T1.3): unhealthy/503 while the database is unreachable or the journal
/// head is not this build's expected head, healthy/200 otherwise (AC3). The
/// outcome fields land in <see cref="HealthCheckResult.Data"/> so the response
/// writer can emit them without re-parsing the description.
/// </summary>
public sealed class ReadinessHealthCheck : IHealthCheck
{
    /// <summary>Name of the readiness health check in the health check registry.</summary>
    public const string Name = "ready";

    private readonly ReadinessProbe _probe;

    /// <summary>Creates the health check over the shared readiness probe.</summary>
    public ReadinessHealthCheck(ReadinessProbe probe) => _probe = probe;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _probe.CheckAsync(cancellationToken);

        var data = new Dictionary<string, object>
        {
            ["reason"] = result.Reason,
            ["expected_head"] = result.ExpectedHead,
        };
        if (result.JournalHead is not null)
        {
            data["journal_head"] = result.JournalHead;
        }

        return result.Ready
            ? HealthCheckResult.Healthy($"{result.Reason}: journal head {result.JournalHead}", data)
            : new HealthCheckResult(
                HealthStatus.Unhealthy,
                $"{result.Reason}: journal head {result.JournalHead ?? "none"}, expected {result.ExpectedHead}",
                data: data);
    }
}
