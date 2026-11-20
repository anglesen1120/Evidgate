using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// The filter vocabularies of route 22 and the shape selector of its page statement, in one place so the served
/// statement and its predicates cannot drift.
/// </summary>
internal static class AuthoringVersionFilters
{
    /// <summary>The three <c>entity_kind</c> values of AC9, spelled as the column stores them.</summary>
    internal static readonly string[] EntityKinds =
        [.. Enum.GetValues<AuthoringEntityKind>().Select(AuthoringWireValues.EntityKindName)];

    /// <summary>The three <c>source</c> values of AC9, spelled as the column stores them.</summary>
    internal static readonly string[] Sources =
        [.. Enum.GetValues<AuthoringSource>().Select(AuthoringWireValues.SourceName)];
}
