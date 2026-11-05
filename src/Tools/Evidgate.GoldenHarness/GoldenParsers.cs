using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

namespace Evidgate.GoldenHarness;

/// <summary>
/// The golden corpus's parser table: one in-process entry point per producer whose mapper has landed, so a
/// fixture is replayed through the same mapping code the ingest path runs rather than through a copy of it.
/// <para>
/// A producer is registered here only when its mapper exists; a case whose parser is absent and marked
/// <c>pending</c> reports <c>PENDING</c>, which is not a pass. The corpus is producer-agnostic: this class is
/// the only place that knows which dialects the repository implements.
/// </para>
/// </summary>
public static class GoldenParsers
{
    /// <summary>The pytest <c>junitxml</c> dialect, which is also its fixture directory name.</summary>
    public const string PytestParserId = "junit-pytest";

    /// <summary>Builds the registry the console harness runs with.</summary>
    /// <returns>A registry holding every mapper this repository implements.</returns>
    public static ParserRegistry Create() =>
        new ParserRegistry().Register(PytestParserId, PytestFixture);

    /// <summary>
    /// Replays one pytest case. The case identity is the producer's own identity hint: a dotted
    /// <c>classname</c> is pytest's module path, and a path-shaped one is already a file. The rule belongs to the
    /// producer's mapping document; the harness pins it only so one golden case can name an expected case key.
    /// </summary>
    private static ExpectedDoc PytestFixture(FixtureCase fixture)
    {
        if (fixture.ReportFiles.Count != 1)
        {
            throw new InvalidOperationException(
                $"{fixture.Producer}/{fixture.Name}: the pytest dialect reads exactly one report file, "
                + $"but the case holds {fixture.ReportFiles.Count}");
        }

        var path = Path.Combine(fixture.Directory, fixture.ReportFiles[0]);
        var mapper = PytestJUnitMapper.Create(new ParsingService());

        JUnitMapResult mapped;
        try
        {
            using var report = File.OpenRead(path);

            // The harness holds no artifact row, so the raw report identity is the empty id; the mapper only
            // echoes it, and the corpus compares mapping facts (case, status, sequence), not row identity.
            mapped = mapper.Map(report, new JUnitMapContext(Guid.Empty, CaseKey));
        }
        catch (JUnitParseRefusedException refusal)
        {
            // The runner decides whether a refusal was declared by the fixture's own expected status.
            throw new ParseRefusedException($"{refusal.Reason}: {refusal.Message}");
        }

        return new ExpectedDoc(
            "parsed",
            mapped.Declared.Tests ?? mapped.Cases.Count,
            mapped.Cases
                .Select(mappedCase => new NormalisedEvent(
                    fixture.Meta.ProducerId,
                    mappedCase.Sequence,
                    mappedCase.ExecutionKey,
                    mappedCase.RawStatus,
                    mappedCase.Normalized))
                .ToList());
    }

    /// <summary>The pytest node id a case identity hint resolves to, or <see langword="null"/> when it names nothing.</summary>
    private static string? CaseKey(string classname, string name)
    {
        if (classname.Length == 0)
        {
            return name.Length == 0 ? null : name;
        }

        if (classname.Contains('/') || classname.EndsWith(".py", StringComparison.Ordinal))
        {
            return classname + "::" + name;
        }

        return classname.Replace('.', '/') + ".py::" + name;
    }
}
