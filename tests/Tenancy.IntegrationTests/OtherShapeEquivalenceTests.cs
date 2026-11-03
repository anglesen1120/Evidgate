using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Evidence.Application.Freeze;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The identifier shapes that a timed <see cref="Regex"/> answered before they were replaced by one forward pass over
/// a <c>SearchValues</c> set. Each case keeps the retired pattern as an oracle with no timeout and asserts the two
/// agree on every corpus value. The one documented difference is a single trailing <c>'\n'</c>: a <c>$</c>-anchored
/// pattern also accepted it, the converted shape refuses it exactly as <c>\z</c> does.
/// </summary>
public sealed class OtherShapeEquivalenceTests
{
    private sealed record Shape(string Name, Regex Oracle, Func<string, bool> Converted, bool DollarAnchored);

    private static readonly Shape ReasonCode = new(
        "reason code",
        new Regex(@"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant),
        value => EvidenceFreeze.IsReasonCode(value),
        DollarAnchored: false);

    private static readonly Shape EnvironmentKey = new(
        "environment key",
        new Regex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant),
        value => CreateEnvironmentRevisionCommandHandler.IsEnvironmentKey(value),
        DollarAnchored: true);

    private static readonly Shape SecretReference = new(
        "secret reference",
        new Regex("^secret:[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant),
        value => SecretShape.IsReference(value),
        DollarAnchored: true);

    private static readonly Shape AdapterName = new(
        "adapter name",
        new Regex(@"^[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant),
        value => Evidgate.Modules.Ingestion.Domain.AdapterName.IsValid(value),
        DollarAnchored: false);

    // Valid forms, both length bounds and one over, every character class boundary, and the lookalikes a culture-aware
    // read would wrongly accept: fullwidth Latin (U+FF41), Cyrillic (U+0430), control characters and a NUL.
    private static readonly string[] ReasonCodes =
    [
        "", "a", "z", "a0", "_", "abc-", "abc ", " abc", "1abc", "_abc", "-abc", "x.y",
        "run_failed", "artifact_missing", "a_b_c_1", "sha256", "a" + new string('b', 62),
        "a" + new string('b', 63), "a" + new string('b', 64), new string('a', 64), new string('a', 65),
        "Abc", "ABC", "aBc", "a_bC",
        "abc\n", "abc\n\n", "abc\r\n", "abc\t", "abc\0", "ab\0c", "abc ",
        "аbc", "а", "ａbc", "abc\u00e9", "abc😀",
    ];

    private static readonly string[] EnvironmentKeys =
    [
        "", "a", "1", "0", "p-", "p_", "_p", "-p", ".p", "prod", "prod-1", "prod_1", "prod-1_x",
        "a" + new string('b', 62), "a" + new string('b', 63), "a" + new string('b', 64),
        new string('a', 64), new string('9', 65),
        "PROD", "Prod", "prod store", "prod.store", "prod/store", "prod@1",
        "prod\n", "prod\n\n", "prod\r\n", "prod\t", "prod\0", "pro\0d",
        "рrod", "ｐrod", "prod\u00e9", "prod😀",
    ];

    private static readonly string[] SecretReferences =
    [
        "", "secret", "secret:", "Secret:name", "SECRET:name", "secretx:name", "sec ret:name",
        "secret:a", "secret:A", "secret:0", "secret:_", "secret:-", "secret:.", "secret:name",
        "secret:NAME_1-x.y", "secret:database", "secret:" + new string('a', 63),
        "secret:" + new string('a', 64), "secret:" + new string('a', 65),
        "secret:name ", " secret:name", "secret:na me", "secret:na/me", "secret:na@me", "secret:na:me",
        "secret:name\n", "secret:name\n\n", "secret:name\r\n", "secret:name\t", "secret:name\0", "secret:na\0me",
        "secret:ｎame", "secret:паме", "secret:name😀",
    ];

    private static readonly string[] AdapterNames =
    [
        "", "a", "1", "a0", "a.", "a-", "a_", "a._-", "-a", ".a", "_a", "A", "Playwright", "PLAY",
        "playwright", "play.wright", "play-wright_1", "a" + new string('b', 63), "a" + new string('b', 64),
        new string('a', 64), new string('a', 65),
        "play wright", "play/wright", "play@wright",
        "play\n", "play\n\n", "play\r\n", "play\t", "play\0", "pla\0y",
        "рlay", "ｐlay", "play\u00e9", "play😀",
    ];

    /// <summary>The corpus is never empty and every accept/refuse decision matches the retired pattern.</summary>
    [Fact]
    public void ConvertedShapes_MatchTheRetiredPattern_OnEveryCorpusValue()
    {
        var differences = new List<string>();
        var checkedValues = 0;
        var trailingNewlines = 0;

        foreach (var (shape, corpus) in Cases())
        {
            corpus.Length.ShouldBeGreaterThan(30, $"{shape.Name}: the corpus must carry the boundaries, not a sample");

            foreach (var value in corpus)
            {
                checkedValues++;
                var oracle = shape.Oracle.IsMatch(value);
                var converted = shape.Converted(value);
                if (oracle == converted)
                {
                    continue;
                }

                if (shape.DollarAnchored && oracle && !converted && value.EndsWith('\n'))
                {
                    trailingNewlines++;
                    continue;
                }

                differences.Add($"{shape.Name}: {Describe(value)} oracle={oracle} converted={converted}");
            }
        }

        checkedValues.ShouldBeGreaterThan(100);
        differences.ShouldBeEmpty(string.Join(Environment.NewLine, differences));
        trailingNewlines.ShouldBeGreaterThan(1, "the corpus must contain the one documented difference");
    }

    /// <summary>A trailing newline is refused by the converted shapes; the retired <c>$</c> patterns accepted it.</summary>
    [Fact]
    public void ConvertedShapes_RefuseOneTrailingNewline()
    {
        (Shape Shape, string Value)[] cases =
        [
            (ReasonCode, "reason_code\n"),
            (EnvironmentKey, "prod-1\n"),
            (SecretReference, "secret:name\n"),
            (AdapterName, "playwright\n"),
        ];

        foreach (var (shape, value) in cases)
        {
            shape.Oracle.IsMatch(value).ShouldBe(
                shape.DollarAnchored,
                $"{shape.Name}: only a `$`-anchored pattern accepted the trailing newline");
            shape.Converted(value).ShouldBeFalse($"{shape.Name}: one trailing newline is refused");
        }
    }

    /// <summary>Hostile lengths are refused in one bounded pass, with no wall clock (and so no timeout) involved.</summary>
    [Fact]
    public void ConvertedShapes_RefuseHostileLengths_WithinOneSecond()
    {
        var hostile = new string('a', 1_000_000);
        var hostileTail = new string('a', 100_000) + "A";
        var hostileReference = "secret:" + hostile;

        (Shape Shape, string[] Values)[] cases =
        [
            (ReasonCode, [hostile, hostileTail]),
            (EnvironmentKey, [hostile, hostileTail]),
            (SecretReference, [hostileReference, "secret:" + hostileTail]),
            (AdapterName, [hostile, hostileTail]),
        ];

        foreach (var (shape, values) in cases)
        {
            foreach (var value in values)
            {
                var stopwatch = Stopwatch.StartNew();
                var accepted = shape.Converted(value);
                stopwatch.Stop();

                accepted.ShouldBeFalse(
                    shape.Name
                    + " refuses "
                    + value.Length.ToString(CultureInfo.InvariantCulture)
                    + " characters");
                stopwatch.Elapsed.ShouldBeLessThan(
                    TimeSpan.FromSeconds(1),
                    $"{shape.Name}: a hostile input is decided by the length bound, not by scanning it");
            }
        }
    }

    /// <summary>Both Orchestrator call sites read a reference through the one shared shape.</summary>
    [Fact]
    public void OrchestratorCallSites_ReadTheSharedReferenceShape()
    {
        AcceptsReference("secret:name").ShouldBeTrue();
        AcceptsReference("secret:database").ShouldBeTrue();
        AcceptsReference("secret:").ShouldBeFalse();
        AcceptsReference("secret:" + new string('a', 65)).ShouldBeFalse();
        AcceptsReference("secret:name\n").ShouldBeFalse();

        ManifestVerifier.IsSecret("param", "secret:name").ShouldBeFalse();
        ManifestVerifier.IsSecret("param", "secret:name\n").ShouldBeTrue();
        ManifestVerifier.IsSecret("param", "secret:").ShouldBeTrue();
    }

    private static bool AcceptsReference(string reference)
    {
        var request = new CreateEnvironmentRevisionRequest(
            null,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [reference]);

        var (configuration, _) = EnvironmentRevisionRules.Check(request);
        return configuration is not null;
    }

    private static (Shape Shape, string[] Corpus)[] Cases() =>
    [
        (ReasonCode, ReasonCodes),
        (EnvironmentKey, EnvironmentKeys),
        (SecretReference, SecretReferences),
        (AdapterName, AdapterNames),
    ];

    private static string Describe(string value)
    {
        var text = new StringBuilder();
        foreach (var character in value.AsSpan(0, Math.Min(value.Length, 60)))
        {
            if (character is >= ' ' and <= '~')
            {
                text.Append(character);
                continue;
            }

            text.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
        }

        var described = new StringBuilder()
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" chars \"")
            .Append(text);

        if (value.Length > 60)
        {
            described.Append('…');
        }

        return described.Append('"').ToString();
    }
}
