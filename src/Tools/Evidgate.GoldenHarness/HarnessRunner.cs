using System.Globalization;
using System.Text;

namespace Evidgate.GoldenHarness;

/// <summary>
/// Replays every fixture through the parser its <c>case.json</c> names and prints one
/// verdict per case plus a summary. Exit codes: 0 when no case failed or errored, 1 when
/// any case failed, errored or the fixture layout was refused, and 2 on a usage error.
/// A pending parser is reported as <c>PENDING</c> and fails the run only under <c>--strict</c>.
/// </summary>
public static class HarnessRunner
{
    private const string Usage =
        "usage: Evidgate.GoldenHarness --fixtures <root> [--producer <producer>] [--strict]";

    public static int Run(string[] args, IParserRegistry registry, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(output);

        var options = ParseArguments(args, out var error);
        if (options is null)
        {
            output.WriteLine(error);
            output.WriteLine(Usage);
            return 2;
        }

        List<CaseVerdict> verdicts;
        try
        {
            verdicts = FixtureLayout.Scan(options.FixturesRoot, options.Producer)
                .Select(fixture => Evaluate(fixture, registry))
                .ToList();
        }
        catch (FixtureLayoutException exception)
        {
            verdicts =
            [
                new CaseVerdict(exception.Fixture, Verdict.Error, [], exception.Message),
            ];
        }

        WriteVerdicts(output, verdicts);
        WriteSummary(output, verdicts);
        return ExitCode(verdicts, options.Strict);
    }

    private static CaseVerdict Evaluate(FixtureCase fixture, IParserRegistry registry)
    {
        var label = $"{fixture.Producer}/{fixture.Name}";

        if (!registry.TryGet(fixture.Meta.ParserId, out var parse))
        {
            return IsPending(fixture.Meta)
                ? new CaseVerdict(
                    label,
                    Verdict.Pending,
                    [],
                    $"parser '{fixture.Meta.ParserId}' has not landed yet")
                : new CaseVerdict(
                    label,
                    Verdict.Error,
                    [],
                    $"parser '{fixture.Meta.ParserId}' is not registered and the case is not marked pending");
        }

        ExpectedDoc observed;
        try
        {
            observed = parse(fixture);
        }
        catch (ParseRefusedException exception)
        {
            return Refused(label, fixture.Expected, exception);
        }
        catch (Exception exception)
        {
            return new CaseVerdict(label, Verdict.Error, [], $"{exception.GetType().Name}: {exception.Message}");
        }

        var diffs = GoldenDiff.Compare(fixture.Expected, observed);
        return diffs.Count == 0
            ? new CaseVerdict(label, Verdict.Pass, [], null)
            : new CaseVerdict(label, Verdict.Fail, diffs, "produced events differ from expected.json");
    }

    /// <summary>
    /// A parser that refuses the bytes is the declared outcome only when the expectation says
    /// so; otherwise the refusal is a failure, so a truncated report can never pass silently.
    /// </summary>
    private static CaseVerdict Refused(string label, ExpectedDoc expected, ParseRefusedException exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}";
        var declaredMalformed = string.Equals(
                expected.RawReportStatus,
                "malformed",
                StringComparison.OrdinalIgnoreCase)
            && expected.Events.Count == 0;

        if (!declaredMalformed)
        {
            return new CaseVerdict(
                label,
                Verdict.Fail,
                [new DiffEntry(null, "raw_report_status", DiffReasons.Malformed, expected.RawReportStatus, "malformed")],
                message);
        }

        var observed = new ExpectedDoc("malformed", 0, []);
        var diffs = GoldenDiff.Compare(expected, observed);
        return diffs.Count == 0
            ? new CaseVerdict(label, Verdict.Pass, [], $"declared malformed; parser refused the report ({exception.GetType().Name})")
            : new CaseVerdict(label, Verdict.Fail, diffs, message);
    }

    private static bool IsPending(CaseMeta meta) =>
        string.Equals(meta.ParserStatus, "pending", StringComparison.Ordinal);

    private static void WriteVerdicts(TextWriter output, List<CaseVerdict> verdicts)
    {
        var width = verdicts.Count == 0
            ? "fixture".Length
            : Math.Max("fixture".Length, verdicts.Max(verdict => EscapeControls(verdict.Fixture).Length));

        var header = "fixture".PadRight(width);
        output.WriteLine($"{header}  verdict  detail");
        foreach (var verdict in verdicts)
        {
            var row = $"{EscapeControls(verdict.Fixture).PadRight(width)}  {Word(verdict.Verdict),-7}";
            if (!string.IsNullOrEmpty(verdict.Message))
            {
                row = $"{row}  {EscapeControls(verdict.Message)}";
            }

            output.WriteLine(row.TrimEnd());

            foreach (var diff in verdict.Diffs)
            {
                output.WriteLine(FormatDiff(verdict.Fixture, diff));
            }
        }
    }

    private static string FormatDiff(string fixture, DiffEntry diff)
    {
        var index = diff.EventIndex is { } value
            ? value.ToString(CultureInfo.InvariantCulture)
            : "-";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"  diff {EscapeControls(fixture)}: event {index} field {EscapeControls(diff.Field)} ({EscapeControls(diff.Reason)}) expected={Show(diff.Expected)} observed={Show(diff.Observed)}");
    }

    private static string Show(string? value) => value is null ? "<null>" : EscapeControls(value);

    private static string EscapeControls(string value)
    {
        var firstControl = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
            {
                firstControl = index;
                break;
            }
        }

        if (firstControl < 0)
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length).Append(value, 0, firstControl);
        for (var index = firstControl; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character))
            {
                escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }

    private static string Word(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "PASS",
        Verdict.Fail => "FAIL",
        Verdict.Pending => "PENDING",
        Verdict.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "unknown verdict"),
    };

    private static void WriteSummary(TextWriter output, List<CaseVerdict> verdicts)
    {
        var passed = verdicts.Count(verdict => verdict.Verdict == Verdict.Pass);
        var failed = verdicts.Count(verdict => verdict.Verdict == Verdict.Fail);
        var pending = verdicts.Count(verdict => verdict.Verdict == Verdict.Pending);
        var errors = verdicts.Count(verdict => verdict.Verdict == Verdict.Error);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Summary: {passed} passed, {failed} failed, {pending} pending, {errors} error; pending cases are not passes"));
    }

    private static int ExitCode(List<CaseVerdict> verdicts, bool strict)
    {
        var blocked = verdicts.Any(verdict => verdict.Verdict is Verdict.Fail or Verdict.Error)
            || (strict && verdicts.Any(verdict => verdict.Verdict == Verdict.Pending));

        return blocked ? 1 : 0;
    }

    private static Options? ParseArguments(string[] args, out string error)
    {
        string? fixturesRoot = null;
        string? producer = null;
        var strict = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fixtures":
                    if (!TryReadValue(args, ref index, "--fixtures", out var root, out error))
                    {
                        return null;
                    }

                    if (fixturesRoot is not null)
                    {
                        error = "--fixtures is given more than once";
                        return null;
                    }

                    fixturesRoot = root;
                    break;

                case "--producer":
                    if (!TryReadValue(args, ref index, "--producer", out var name, out error))
                    {
                        return null;
                    }

                    if (producer is not null)
                    {
                        error = "--producer is given more than once";
                        return null;
                    }

                    producer = name;
                    break;

                case "--strict":
                    strict = true;
                    break;

                default:
                    error = $"unknown argument '{args[index]}'";
                    return null;
            }
        }

        if (fixturesRoot is null)
        {
            error = "--fixtures <root> is required";
            return null;
        }

        error = string.Empty;
        return new Options(fixturesRoot, producer, strict);
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string option,
        out string value,
        out string error)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"{option} requires a value";
            return false;
        }

        index++;
        value = args[index];

        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{option} requires a non-empty value";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private sealed record Options(string FixturesRoot, string? Producer, bool Strict);
}
