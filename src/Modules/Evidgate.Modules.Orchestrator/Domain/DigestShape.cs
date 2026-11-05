using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// The one content-addressed digest shape the Orchestrator's wire bodies and the SQL <c>CHECK</c>s share (00 §7):
/// <c>sha256:</c> + 64 lower-case hex characters. One forward pass over at most 71 characters and no regex, so a slow
/// host cannot turn a refusal into a throw (00 §5.7 N11).
/// </summary>
internal static class DigestShape
{
    /// <summary>The prefix every content-addressed digest carries (00 §7, <c>manifest_hash</c> included).</summary>
    internal const string Prefix = "sha256:";

    /// <summary>The number of hex characters after <see cref="Prefix"/>.</summary>
    internal const int HexLength = 64;

    /// <summary>The whole digest's length: the seven characters of <see cref="Prefix"/> and the 64 hex characters.</summary>
    internal const int TotalLength = 71;

    /// <summary>True when <paramref name="value"/> is exactly <c>sha256:</c> + 64 lower-case hex characters.</summary>
    internal static bool IsSha256(string? value) =>
        value is { Length: TotalLength } digest
        && digest.StartsWith(Prefix, StringComparison.Ordinal)
        && AsciiShape.IsMatch(digest.AsSpan(Prefix.Length), HexLength, HexLength, AsciiShape.LowerHex, AsciiShape.LowerHex);
}
