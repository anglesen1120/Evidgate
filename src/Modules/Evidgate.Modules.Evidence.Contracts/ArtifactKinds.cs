namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The closed list of <c>artifact.kind</c>: the <c>artifact</c> table's CHECK and the contract carry this exact
/// vocabulary. It lives here because a caller that resolves a requirement set — the reconciler, and the policy
/// evaluator reading a policy's required kinds — needs the same list, and a second copy would drift.
/// </summary>
public static class ArtifactKinds
{
    /// <summary>A Playwright trace archive.</summary>
    public const string Trace = "trace";

    /// <summary>A recorded video of the attempt.</summary>
    public const string Video = "video";

    /// <summary>A screenshot of the page or app under test.</summary>
    public const string Screenshot = "screenshot";

    /// <summary>A raw runner report.</summary>
    public const string RawReport = "raw_report";

    /// <summary>A serialised DOM snapshot.</summary>
    public const string DomSnapshot = "dom_snapshot";

    /// <summary>An accessibility snapshot.</summary>
    public const string A11ySnapshot = "a11y_snapshot";

    /// <summary>A patch of the code under test.</summary>
    public const string Patch = "patch";

    /// <summary>A UI hierarchy dump.</summary>
    public const string UiHierarchy = "ui_hierarchy";

    /// <summary>The eight kinds, in contract order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Trace, Video, Screenshot, RawReport, DomSnapshot, A11ySnapshot, Patch, UiHierarchy];

    /// <summary>True when <paramref name="kind"/> is one of the eight contract kinds.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
