using System.Buffers;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The adapter-name grammar: the shape the <c>adapter_capability_name_check</c> column CHECK enforces (migration 0026)
/// and the <c>ext.schema</c> prefix carries on the first-send path. One definition, so the direct registration command
/// and the first-send extractor cannot drift.
/// </summary>
internal static class AdapterName
{
    /// <summary>The name body: a lowercase letter or digit, then up to 63 of <c>[a-z0-9._-]</c>.</summary>
    internal const string Pattern = "[a-z0-9][a-z0-9._-]{0,63}";

    private static readonly SearchValues<char> NameTail = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789._-");

    /// <summary>
    /// True when <paramref name="name"/> is a storable adapter name, in one forward pass with no wall-clock setting,
    /// so a slow host cannot turn the refusal into an exception. A trailing newline is refused, as the CHECK refuses it.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    internal static bool IsValid(string? name) =>
        AsciiShape.IsMatch(name, 1, 64, AsciiShape.LowerAlnum, NameTail);
}
