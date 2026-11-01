namespace Evidgate.Evaluation;

/// <summary>
/// Freezes a caller-owned collection into a read-only copy, so nothing the caller does to its own list afterwards can
/// change an input record and a re-evaluation stays identical.
/// </summary>
internal static class InputSnapshot
{
    /// <summary>Copies the collection, or keeps <c>null</c> when the property was assigned one.</summary>
    public static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? values) =>
        values is null ? null! : Array.AsReadOnly([.. values]);
}
