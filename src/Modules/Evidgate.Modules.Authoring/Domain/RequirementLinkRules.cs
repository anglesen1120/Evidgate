namespace Evidgate.Modules.Authoring.Domain;

/// <summary>The vocabulary and pure rules of a version-pinned requirement link and of its coverage view (migration 0138).</summary>
internal static class RequirementLinkRules
{
    /// <summary>The case is named by a link of its own.</summary>
    internal const string PathDirect = "direct";

    /// <summary>The case is reached only because its checklist item is linked.</summary>
    internal const string PathViaItem = "via_item";

    /// <summary>The case is linked directly and through its item; it still counts once.</summary>
    internal const string PathBoth = "both";

    /// <summary>
    /// The answer for the latest manual result of a case. No result store exists yet, so the view says so out loud:
    /// a missing figure must never read as "no failures".
    /// </summary>
    internal const string ResultUnavailable = "unavailable";

    /// <summary>The most pinned targets one coverage answer lists; the counts always cover every link.</summary>
    internal const int MaximumListedTargets = 500;

    /// <summary>True when exactly one of the two targets is named, the same rule as the table's CHECK.</summary>
    internal static bool HasExactlyOneTarget(Guid? checklistItemId, Guid? manualTestCaseId) =>
        checklistItemId.HasValue != manualTestCaseId.HasValue;

    /// <summary>How a case reached the requirement, from its direct link and its item's link.</summary>
    /// <param name="direct">A link names the case itself.</param>
    /// <param name="viaItem">A link names the case's checklist item.</param>
    internal static string PathOf(bool direct, bool viaItem) =>
        direct && viaItem ? PathBoth : direct ? PathDirect : PathViaItem;
}
