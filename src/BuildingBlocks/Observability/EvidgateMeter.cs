using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// Owns the one platform <see cref="Meter"/>. It lives in its own type so the instrument fields of every
/// <see cref="EvidgateTelemetry"/> partial can read it from a static initialiser whatever order the compiler
/// receives the files in: the CLR runs this type's initialiser on first access, never a partial's.
/// </summary>
internal static class EvidgateMeter
{
    public static readonly Meter Instance = new(EvidgateTelemetry.MeterName);
}
