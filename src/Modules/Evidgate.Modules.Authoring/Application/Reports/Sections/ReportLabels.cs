namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>
/// Every label a section shows, in the language the request pinned. The labels are written into the snapshot, so a
/// renderer never translates and an old report keeps its wording when this table changes.
/// </summary>
internal sealed class ReportLabels
{
    internal static readonly ReportLabels En = new()
    {
        PlanTitle = "Test plan",
        CasesTitle = "Test cases",
        TraceabilityTitle = "Traceability matrix",
        ExecutionTitle = "Execution",
        Scope = "Scope",
        Requirements = "Requirements",
        Priorities = "Priorities",
        Strategy = "Strategy",
        Environment = "Environment",
        NotProvided = "Not provided",
        Precondition = "Precondition",
        Step = "Step",
        Action = "Action",
        Data = "Data",
        Expected = "Expected result",
        NoFeature = "No feature",
        LinkedRequirements = "Linked requirements",
        StaleRequirements = "Requirements linked at a superseded version",
        Covered = "Covered",
        UncoveredRequirements = "Uncovered requirements",
        UnlinkedCases = "Unlinked cases",
        StaleLinks = "Stale links",
        StaleNote = "Linked to a superseded requirement version; not counted as coverage",
        PinnedVersion = "Linked version",
        CurrentVersion = "Current version",
        NoReleaseDecision = "no release decision",
        NoManualExecution = "No manual execution recorded",
        ReleaseDecision = "Release decision",
        Reasons = "Reasons",
        DecisionAbsent = "No release decision is recorded: aggregation is pending or has not produced one",
        ManualResults = "Manual results",
        Passed = "Passed",
        Failed = "Failed",
        Blocked = "Blocked",
        Skipped = "Skipped",
        NotExecuted = "Not executed",
        FeatureNodes = "Feature nodes",
        RequirementScope = "Requirements",
        ManualRun = "Manual run",
        Release = "Release",
    };

    internal static readonly ReportLabels Vi = new()
    {
        PlanTitle = "Kế hoạch kiểm thử",
        CasesTitle = "Ca kiểm thử",
        TraceabilityTitle = "Ma trận truy vết",
        ExecutionTitle = "Thực thi",
        Scope = "Phạm vi",
        Requirements = "Yêu cầu",
        Priorities = "Mức ưu tiên",
        Strategy = "Chiến lược kiểm thử",
        Environment = "Môi trường",
        NotProvided = "Chưa cung cấp",
        Precondition = "Điều kiện tiên quyết",
        Step = "Bước",
        Action = "Thao tác",
        Data = "Dữ liệu",
        Expected = "Kết quả mong đợi",
        NoFeature = "Không thuộc tính năng nào",
        LinkedRequirements = "Yêu cầu liên kết",
        StaleRequirements = "Yêu cầu liên kết ở phiên bản đã bị thay thế",
        Covered = "Đã có kiểm thử",
        UncoveredRequirements = "Yêu cầu chưa có kiểm thử",
        UnlinkedCases = "Ca kiểm thử chưa liên kết yêu cầu",
        StaleLinks = "Liên kết lỗi thời",
        StaleNote = "Liên kết với phiên bản yêu cầu đã bị thay thế; không tính là độ phủ",
        PinnedVersion = "Phiên bản đã liên kết",
        CurrentVersion = "Phiên bản hiện tại",
        NoReleaseDecision = "không có quyết định phát hành",
        NoManualExecution = "Chưa ghi nhận lượt thực thi thủ công nào",
        ReleaseDecision = "Quyết định phát hành",
        Reasons = "Lý do",
        DecisionAbsent = "Chưa có quyết định phát hành: việc tổng hợp đang chờ hoặc chưa tạo ra quyết định",
        ManualResults = "Kết quả thủ công",
        Passed = "Đạt",
        Failed = "Không đạt",
        Blocked = "Bị chặn",
        Skipped = "Bỏ qua",
        NotExecuted = "Chưa thực thi",
        FeatureNodes = "Nút tính năng",
        RequirementScope = "Yêu cầu",
        ManualRun = "Lượt chạy thủ công",
        Release = "Bản phát hành",
    };

    internal required string PlanTitle { get; init; }
    internal required string CasesTitle { get; init; }
    internal required string TraceabilityTitle { get; init; }
    internal required string ExecutionTitle { get; init; }
    internal required string Scope { get; init; }
    internal required string Requirements { get; init; }
    internal required string Priorities { get; init; }
    internal required string Strategy { get; init; }
    internal required string Environment { get; init; }
    internal required string NotProvided { get; init; }
    internal required string Precondition { get; init; }
    internal required string Step { get; init; }
    internal required string Action { get; init; }
    internal required string Data { get; init; }
    internal required string Expected { get; init; }
    internal required string NoFeature { get; init; }
    internal required string LinkedRequirements { get; init; }
    internal required string StaleRequirements { get; init; }
    internal required string Covered { get; init; }
    internal required string UncoveredRequirements { get; init; }
    internal required string UnlinkedCases { get; init; }
    internal required string StaleLinks { get; init; }
    internal required string StaleNote { get; init; }
    internal required string PinnedVersion { get; init; }
    internal required string CurrentVersion { get; init; }
    internal required string NoReleaseDecision { get; init; }
    internal required string NoManualExecution { get; init; }
    internal required string ReleaseDecision { get; init; }
    internal required string Reasons { get; init; }
    internal required string DecisionAbsent { get; init; }
    internal required string ManualResults { get; init; }
    internal required string Passed { get; init; }
    internal required string Failed { get; init; }
    internal required string Blocked { get; init; }
    internal required string Skipped { get; init; }
    internal required string NotExecuted { get; init; }
    internal required string FeatureNodes { get; init; }
    internal required string RequirementScope { get; init; }
    internal required string ManualRun { get; init; }
    internal required string Release { get; init; }

    /// <summary>The labels of <paramref name="language"/> (<c>vi</c> or <c>en</c>); the request validation already refused any other.</summary>
    /// <param name="language">The request language.</param>
    internal static ReportLabels For(string language) => language switch
    {
        "vi" => Vi,
        "en" => En,
        _ => throw new ReportBuildException(ReportBuildException.Failed, "the report language has no labels"),
    };

    /// <summary>The display name of a scope kind.</summary>
    /// <param name="kind">One of <see cref="ReportScopeKinds"/>.</param>
    internal string ScopeKind(string kind) => kind switch
    {
        ReportScopeKinds.FeatureNodes => FeatureNodes,
        ReportScopeKinds.Requirements => RequirementScope,
        ReportScopeKinds.ManualRun => ManualRun,
        ReportScopeKinds.Release => Release,
        _ => kind,
    };

    /// <summary>The display name of a manual result status, or null for a status the contract does not name.</summary>
    /// <param name="status">A manual result status or <c>not_executed</c>.</param>
    internal string? Status(string status) => status switch
    {
        ManualStatuses.Passed => Passed,
        ManualStatuses.Failed => Failed,
        ManualStatuses.Blocked => Blocked,
        ManualStatuses.Skipped => Skipped,
        ManualStatuses.NotExecuted => NotExecuted,
        _ => null,
    };
}

/// <summary>The manual result statuses a report counts; a case with no result is in its own bucket, never in one of these.</summary>
internal static class ManualStatuses
{
    internal const string Passed = "passed";
    internal const string Failed = "failed";
    internal const string Blocked = "blocked";
    internal const string Skipped = "skipped";
    internal const string NotExecuted = "not_executed";

    /// <summary>The order the counts are shown in.</summary>
    internal static readonly string[] Ordered = [Passed, Failed, Blocked, Skipped, NotExecuted];
}
