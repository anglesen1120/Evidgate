using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Foundation.IntegrationTests;

/// <summary>
/// The .NET half of the large-report fixture: the repository stores the generator and
/// a loader-shaped directory (<c>meta.json</c>, <c>input.json</c> generator parameters,
/// <c>expected.json</c> checksums), never the report. The tests load that directory, run the generator from
/// its parameters, and pin the bytes to the committed digest — the same pins <c>largeFixture.test.ts</c> asserts on the TypeScript side. Like
/// <see cref="FixtureLoaderTests"/> it needs neither the container nor the database.
/// </summary>
public sealed class LargeFixtureLoaderTests(ITestOutputHelper output)
{
    private static readonly string Generator = Path.Combine(
        FixtureLoader.RepoRoot, "scripts", "dev", "gen-large-report", "gen-large-report.mjs");

    private static readonly Fixture Committed = FixtureLoader.Load(FixtureLoader.Fixtures("large/100k-tests"));

    private static readonly JsonElement Checksums = JsonDocument.Parse(Committed.ExpectedText!).RootElement.Clone();

    private static readonly JsonElement Parameters = JsonDocument.Parse(Committed.InputText).RootElement.Clone();

    private static readonly string[] BudgetMetrics =
    [
        "parse_wall_ms", "reconcile_wall_ms", "walk_wall_ms",
        "child_peak_working_set_bytes", "parent_inflight_retained_bytes",
    ];

    [Fact]
    public void LargeFixture_CommittedDirectory_LoadsWithGeneratorParameters()
    {
        Committed.Domain.ShouldBe("large");
        Committed.Name.ShouldBe("100k-tests");
        Committed.Meta.AddedBy.ShouldBe("04.1.3-T3.1");
        Parameters.GetProperty("seed").GetInt32().ShouldBe(Checksums.GetProperty("seed").GetInt32());
        Parameters.GetProperty("tests").GetInt32().ShouldBe(Checksums.GetProperty("full").GetProperty("tests").GetInt32());
        SmallTests.ShouldBe(Checksums.GetProperty("small").GetProperty("tests").GetInt32());
        AssertBudgetContract(Checksums.GetProperty("budget"));
    }

    [Fact]
    public void LargeFixture_Generator_IsDeterministic_AcrossTwoRuns()
    {
        using var temp = new TempDirectory();
        var firstDirectory = Path.Combine(temp.Path, "a");
        var secondDirectory = Path.Combine(temp.Path, "b");
        var first = Generate(firstDirectory, SmallTests);
        var second = Generate(secondDirectory, SmallTests);

        second.ShouldBe(first);
        first.ShouldBe(PinnedSha256("small"));
        AssertChecksum(Path.Combine(firstDirectory, "input.xml"), PinnedSha256("small"));
        AssertChecksum(Path.Combine(secondDirectory, "input.xml"), PinnedSha256("small"));
    }

    [Fact]
    public void LargeFixture_ChangedReportBytes_DoNotMatchPinnedChecksum()
    {
        using var temp = new TempDirectory();
        Generate(temp.Path, SmallTests);
        var report = Path.Combine(temp.Path, "input.xml");
        File.AppendAllText(report, " ");

        Should.Throw<InvalidOperationException>(
            () => AssertChecksum(report, PinnedSha256("small")));
    }

    [Fact]
    public void LargeFixture_FullReport_MatchesCommittedChecksum()
    {
        using var temp = new TempDirectory();
        var report = Path.Combine(temp.Path, "input.xml");
        var stopwatch = Stopwatch.StartNew();
        var digest = Generate(temp.Path, Parameters.GetProperty("tests").GetInt32());
        stopwatch.Stop();
        var bytes = new FileInfo(report).Length;

        // The measured generation time and size, reported once; no budget is asserted here.
        output.WriteLine($"large-report 100k: {stopwatch.ElapsedMilliseconds} ms, {bytes} bytes, {digest}");
        digest.ShouldBe(PinnedSha256("full"));
        bytes.ShouldBe(Checksums.GetProperty("full").GetProperty("bytes").GetInt64());
        AssertChecksum(report, PinnedSha256("full"));
    }

    [Fact]
    public void LargeFixture_BudgetUnsetOrMeasured_AcceptsContract()
    {
        AssertBudgetContract(JsonDocument.Parse("""{"status":"unset"}""").RootElement.Clone());
        AssertBudgetContract(Element(MeasuredBudget()));
    }

    [Fact]
    public void LargeFixture_BudgetUnsetWithExtraMember_Rejects()
    {
        AssertBudgetContract(JsonDocument.Parse("""{"status":"unset"}""").RootElement.Clone());

        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(
            JsonDocument.Parse("""{"status":"unset","limits":{}}""").RootElement.Clone()));
    }

    [Theory]
    [InlineData("", "measured_on")]
    [InlineData("", "headroom")]
    [InlineData("", "measured")]
    [InlineData("", "limits")]
    [InlineData("measured_on", "date")]
    [InlineData("measured_on", "host")]
    [InlineData("measured_on", "sha")]
    [InlineData("headroom", "wall")]
    [InlineData("headroom", "memory")]
    [InlineData("headroom", "retained")]
    public void LargeFixture_MeasuredBudgetMissingMember_Rejects(string parent, string member)
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        var target = parent.Length == 0 ? budget : budget[parent]!.AsObject();
        target.Remove(member).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    [Theory]
    [InlineData("measured")]
    [InlineData("limits")]
    public void LargeFixture_MeasuredBudgetMissingMetric_Rejects(string section)
    {
        foreach (var metric in BudgetMetrics)
        {
            var budget = MeasuredBudget();
            AssertBudgetContract(Element(budget));
            budget[section]!.AsObject().Remove(metric).ShouldBeTrue();
            Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
        }
    }

    [Theory]
    [InlineData("below_measured")]
    [InlineData("below_headroom")]
    public void LargeFixture_MeasuredBudgetInsufficientLimit_Rejects(string condition)
    {
        foreach (var metric in BudgetMetrics)
        {
            var budget = MeasuredBudget();
            AssertBudgetContract(Element(budget));
            budget["limits"]![metric] = condition == "below_measured" ? 0 : budget["limits"]![metric]!.GetValue<int>() - 1;
            Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
        }
    }

    [Fact]
    public void LargeFixture_BudgetUnknownStatus_Rejects()
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        budget["status"] = "unknown";
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    [Theory]
    [InlineData("measured_on", "host", "\" \"")]
    [InlineData("measured_on", "sha", "\"not-a-sha\"")]
    [InlineData("headroom", "wall", "0")]
    [InlineData("headroom", "wall", "4")]
    [InlineData("headroom", "memory", "-1")]
    [InlineData("headroom", "memory", "3")]
    [InlineData("headroom", "retained", "-1")]
    [InlineData("headroom", "retained", "2")]
    [InlineData("measured", "parse_wall_ms", "1.5")]
    [InlineData("measured", "parent_inflight_retained_bytes", "-1")]
    [InlineData("limits", "walk_wall_ms", "5.5")]
    public void LargeFixture_MeasuredBudgetInvalidMember_Rejects(string section, string member, string value)
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        budget[section]![member] = JsonNode.Parse(value);
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    [Fact]
    public void LargeFixture_MeasuredBudgetShaTrailingNewline_Rejects()
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        budget["measured_on"]!["sha"] = "abcdef12\n";
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    [Fact]
    public void LargeFixture_MeasuredBudgetDateTrailingNewline_Rejects()
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        budget["measured_on"]!["date"] = "2026-11-20\n";
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    [Fact]
    public void LargeFixture_MeasuredBudgetInvalidCalendarDate_Rejects()
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        budget["measured_on"]!["date"] = "2026-02-30";
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    [Fact]
    public void LargeFixture_MeasuredBudgetYear0099_Accepts()
    {
        var budget = MeasuredBudget();
        budget["measured_on"]!["date"] = "0099-01-01";
        AssertBudgetContract(Element(budget));
    }

    [Fact]
    public void LargeFixture_MeasuredBudgetObsoleteMetric_Rejects()
    {
        var budget = MeasuredBudget();
        AssertBudgetContract(Element(budget));
        budget["measured"]!["parent_peak_working_set_bytes"] = 1;
        Should.Throw<InvalidOperationException>(() => AssertBudgetContract(Element(budget)));
    }

    private static JsonObject MeasuredBudget() => JsonNode.Parse("""
        {
          "status": "measured",
          "measured_on": {"date": "2026-11-20", "host": "budget-host", "sha": "abcdef12"},
          "headroom": {"wall": 5, "memory": 2, "retained": 1.05},
          "measured": {
            "parse_wall_ms": 1, "reconcile_wall_ms": 1, "walk_wall_ms": 1,
            "child_peak_working_set_bytes": 1, "parent_inflight_retained_bytes": 1
          },
          "limits": {
            "parse_wall_ms": 5, "reconcile_wall_ms": 5, "walk_wall_ms": 5,
            "child_peak_working_set_bytes": 2, "parent_inflight_retained_bytes": 2
          }
        }
        """)!.AsObject();

    private static JsonElement Element(JsonObject budget) =>
        JsonDocument.Parse(budget.ToJsonString()).RootElement.Clone();

    private static void AssertBudgetContract(JsonElement budget)
    {
        var status = RequiredMember(budget, "status");
        Require(status.ValueKind == JsonValueKind.String, "budget.status must be a string");
        if (status.GetString() == "unset")
        {
            Require(budget.EnumerateObject().Count() == 1, "budget.unset must carry only status");
            return;
        }

        Require(status.GetString() == "measured", "budget.status must be unset or measured");
        var metadata = RequiredMember(budget, "measured_on");
        var date = RequiredString(metadata, "date");
        Require(DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "budget.measured_on.date must be YYYY-MM-DD");
        RequiredString(metadata, "host");
        var sha = RequiredString(metadata, "sha");
        Require(sha.Length >= 8 && sha.All(Uri.IsHexDigit), "budget.measured_on.sha must be 8+ hex digits");
        var headroom = RequiredMember(budget, "headroom");
        var wall = RequiredNumber(headroom, "wall");
        var memory = RequiredNumber(headroom, "memory");
        var retained = RequiredNumber(headroom, "retained");
        Require(wall == 5 && memory == 2 && retained == 1.05, "budget.headroom must be wall 5, memory 2 and retained 1.05");
        var measured = RequiredMember(budget, "measured");
        var limits = RequiredMember(budget, "limits");
        AssertMetricKeys(measured);
        AssertMetricKeys(limits);
        foreach (var metric in BudgetMetrics)
        {
            var value = RequiredMetric(measured, metric);
            var limit = RequiredMetric(limits, metric);
            // The child's resident set varies with host load, so it keeps a loose factor; the parent's retained managed
            // heap repeats within a percent after a forced collection, so only a tight factor sees a buffering regression.
            var factor = metric.EndsWith("_wall_ms", StringComparison.Ordinal) ? wall
                : metric == "parent_inflight_retained_bytes" ? retained : memory;
            Require(limit >= value && limit >= Math.Ceiling(value * factor), $"budget.limits.{metric} is below measured/headroom");
        }
    }

    private static void AssertMetricKeys(JsonElement metrics)
    {
        Require(metrics.ValueKind == JsonValueKind.Object, "budget metrics must be an object");
        Require(metrics.EnumerateObject().Count() == BudgetMetrics.Length &&
            BudgetMetrics.All(metric => metrics.TryGetProperty(metric, out _)), "budget metrics must have exactly five named members");
    }

    private static JsonElement RequiredMember(JsonElement parent, string member)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"budget parent of {member} must be an object");
        Require(parent.TryGetProperty(member, out var value), $"budget missing {member}");
        return value;
    }

    private static string RequiredString(JsonElement parent, string member)
    {
        var value = RequiredMember(parent, member);
        Require(value.ValueKind == JsonValueKind.String, $"budget.{member} must be a string");
        var text = value.GetString();
        Require(!string.IsNullOrWhiteSpace(text), $"budget.{member} must not be blank");
        return text!;
    }

    private static double RequiredNumber(JsonElement parent, string member)
    {
        var value = RequiredMember(parent, member);
        Require(value.ValueKind == JsonValueKind.Number, $"budget.{member} must be a number");
        Require(value.TryGetDouble(out var number) && double.IsFinite(number), $"budget.{member} must be finite");
        return number;
    }

    private static double RequiredMetric(JsonElement parent, string member)
    {
        var value = RequiredNumber(parent, member);
        Require(value >= 0 && Math.Truncate(value) == value, $"budget.{member} must be a nonnegative integer");
        return value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int SmallTests => Parameters.GetProperty("small_tests").GetInt32();

    private static string PinnedSha256(string size) =>
        Checksums.GetProperty(size).GetProperty("sha256").GetString().ShouldNotBeNull();

    /// <summary>
    /// Rejects a report whose on-disk bytes are not the pinned ones: the loaders have no checksum
    /// rule, so the fixture pins them here instead of in <c>expected.json</c> alone.
    /// </summary>
    private static void AssertChecksum(string path, string expected)
    {
        var actual = Sha256Of(path);
        if (actual != expected)
        {
            throw new InvalidOperationException($"{path}: checksum {actual} != pinned {expected}");
        }
    }

    private static string Generate(string outDirectory, int tests)
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Generator);
        start.ArgumentList.Add("--out-dir");
        start.ArgumentList.Add(outDirectory);
        start.ArgumentList.Add($"--tests={tests}");
        start.ArgumentList.Add($"--seed={Parameters.GetProperty("seed").GetInt32()}");

        using var process = Process.Start(start).ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, stderr);
        return JsonDocument.Parse(stdout).RootElement.GetProperty("sha256").GetString()!;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evidgate-large-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
