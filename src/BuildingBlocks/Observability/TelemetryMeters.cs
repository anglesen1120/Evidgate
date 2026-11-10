namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The meters this host subscribes to besides its own. Each name is the exact one the library publishes on;
/// the metrics catalogue pins the instrument names inside them, and the catalogue test subscribes through
/// this class so a library's rename fails a test instead of silently dropping a series.
/// </summary>
public static class TelemetryMeters
{
    /// <summary>The .NET runtime's own meter: GC, JIT, thread-pool and exception instruments.</summary>
    public const string Runtime = "System.Runtime";

    /// <summary>The Npgsql meter: connection-pool occupancy, pool timeouts and operation durations.</summary>
    public const string Npgsql = "Npgsql";

    /// <summary>The ASP.NET Core hosting meter: the server request-duration histogram the ingest SLI reads.</summary>
    public const string AspNetCoreHosting = "Microsoft.AspNetCore.Hosting";
}
