using System.Globalization;
using System.Text.Json;

namespace Tenancy.IntegrationTests;

/// <summary>Whether the fixture's budget has been recorded yet: an <c>unset</c> budget cannot be compared.</summary>
internal enum BudgetStatus
{
    /// <summary>The fixture names no numbers: a run only measures them for the record.</summary>
    Unset,

    /// <summary>The fixture carries the limits a run is compared against.</summary>
    Measured,
}

/// <summary>
/// The five numbers one budget run produces, in the contract's own order: three phase wall times, the parse child's
/// peak resident set and the bytes the parent retains while the parse is in flight.
/// </summary>
internal sealed record BudgetMetrics(
    long ParseWallMs,
    long ReconcileWallMs,
    long WalkWallMs,
    long ChildPeakWorkingSetBytes,
    long ParentInflightRetainedBytes)
{
    /// <summary>The metric names the fixture's <c>budget</c> member declares, in the same order as this record.</summary>
    internal static readonly string[] Names =
    [
        "parse_wall_ms",
        "reconcile_wall_ms",
        "walk_wall_ms",
        "child_peak_working_set_bytes",
        "parent_inflight_retained_bytes",
    ];

    /// <summary>One metric by its contract name; the mapping is the record's own field order.</summary>
    internal long Value(string name) => name switch
    {
        "parse_wall_ms" => ParseWallMs,
        "reconcile_wall_ms" => ReconcileWallMs,
        "walk_wall_ms" => WalkWallMs,
        "child_peak_working_set_bytes" => ChildPeakWorkingSetBytes,
        "parent_inflight_retained_bytes" => ParentInflightRetainedBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a budget metric"),
    };

    /// <summary>One <c>name=value</c> line per metric, so a log line and a failure message quote the same text.</summary>
    internal IEnumerable<string> Lines() =>
        Names.Select(name => string.Create(CultureInfo.InvariantCulture, $"{name}={Value(name)}"));
}

/// <summary>The comparison's answer: whether the run stayed inside the recorded limits, and the numbers either way.</summary>
/// <param name="WithinBudget">True when every metric is at or below its limit (an unset budget is never within).</param>
/// <param name="Status">The budget the comparison read.</param>
/// <param name="Message">The measured-versus-limit text; a failure carries it as its message.</param>
internal sealed record BudgetVerdict(bool WithinBudget, BudgetStatus Status, string Message);

/// <summary>
/// The <c>budget</c> member of a fixture's <c>expected.json</c>: <c>unset</c> until a quiet-host run records the
/// numbers, then five measured values beside the five limits they are compared against.
/// <para>
/// The reader takes only the <c>limits</c> (the measured side, the headroom factors and the commit it was recorded on
/// belong to the fixture's loaders), so a measured budget whose limits are missing a metric, negative or fractional is
/// refused here rather than silently compared against nothing.
/// </para>
/// </summary>
internal sealed class ReportBudget
{
    /// <summary>The failure text an unset budget produces: the numbers a run prints are the ones to record.</summary>
    internal const string RecordTheseNumbers = "budget unset: record these numbers in expected.json";

    private static readonly IReadOnlyDictionary<string, long> NoLimits =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private ReportBudget(BudgetStatus status, IReadOnlyDictionary<string, long> limits)
    {
        Status = status;
        Limits = limits;
    }

    /// <summary>Which state the fixture is in.</summary>
    internal BudgetStatus Status { get; }

    /// <summary>One limit per contract metric; empty while the budget is unset.</summary>
    internal IReadOnlyDictionary<string, long> Limits { get; }

    /// <summary>Reads the <c>budget</c> member of a fixture's <c>expected.json</c> from disk.</summary>
    /// <param name="expectedJsonPath">The fixture's <c>expected.json</c>.</param>
    /// <returns>The budget it declares.</returns>
    internal static ReportBudget Read(string expectedJsonPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedJsonPath);

        using var document = JsonDocument.Parse(File.ReadAllText(expectedJsonPath));

        return Read(document.RootElement);
    }

    /// <summary>Reads the <c>budget</c> member of a parsed <c>expected.json</c>.</summary>
    /// <param name="expected">The file's root object.</param>
    /// <returns>The budget it declares.</returns>
    internal static ReportBudget Read(JsonElement expected) => FromMember(Member(expected, "budget"));

    /// <summary>
    /// Reads the <c>budget</c> member itself: <c>{"status":"unset"}</c>, or a measured budget whose <c>limits</c> carry
    /// one non-negative integer per metric and nothing else.
    /// </summary>
    /// <param name="budget">The member.</param>
    /// <returns>The budget it declares.</returns>
    internal static ReportBudget FromMember(JsonElement budget)
    {
        var status = Member(budget, "status");
        Require(status.ValueKind == JsonValueKind.String, "budget.status must be a string");

        if (string.Equals(status.GetString(), "unset", StringComparison.Ordinal))
        {
            return new ReportBudget(BudgetStatus.Unset, NoLimits);
        }

        Require(string.Equals(status.GetString(), "measured", StringComparison.Ordinal),
            "budget.status must be unset or measured");

        var limits = Member(budget, "limits");
        Require(limits.ValueKind == JsonValueKind.Object, "budget.limits must be an object");
        Require(limits.EnumerateObject().Count() == BudgetMetrics.Names.Length, "budget.limits must carry exactly five metrics");

        var read = new Dictionary<string, long>(BudgetMetrics.Names.Length, StringComparer.Ordinal);
        foreach (var name in BudgetMetrics.Names)
        {
            var value = Member(limits, name);
            long number = 0;
            Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number) && number >= 0,
                string.Create(CultureInfo.InvariantCulture, $"budget.limits.{name} must be a nonnegative integer"));
            read[name] = number;
        }

        return new ReportBudget(BudgetStatus.Measured, read);
    }

    /// <summary>
    /// Compares one run's numbers with this budget. An unset budget is never a pass: it reports the measured numbers so
    /// the timing run has something to record.
    /// </summary>
    /// <param name="measured">The run's five numbers.</param>
    /// <returns>The verdict, whose message quotes every metric it judged.</returns>
    internal BudgetVerdict Compare(BudgetMetrics measured)
    {
        ArgumentNullException.ThrowIfNull(measured);

        if (Status == BudgetStatus.Unset)
        {
            return new BudgetVerdict(
                WithinBudget: false,
                Status,
                $"{RecordTheseNumbers}: {string.Join(", ", measured.Lines())}");
        }

        var over = BudgetMetrics.Names.Where(name => measured.Value(name) > Limits[name]).ToArray();
        var rows = BudgetMetrics.Names.Select(name => string.Create(
            CultureInfo.InvariantCulture,
            $"{name} measured={measured.Value(name)} limit={Limits[name]}"));

        var verdict = over.Length == 0
            ? "within budget"
            : $"budget exceeded by {over.Length} of {BudgetMetrics.Names.Length} metrics";

        return new BudgetVerdict(over.Length == 0, Status, $"{verdict}: {string.Join("; ", rows)}");
    }

    private static JsonElement Member(JsonElement parent, string member)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"budget parent of {member} must be an object");
        Require(parent.TryGetProperty(member, out var value), $"budget is missing {member}");

        return value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
