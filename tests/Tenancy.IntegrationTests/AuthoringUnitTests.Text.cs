using System.Globalization;
using System.Xml.Linq;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T1.2, the text half: NFC composition and the code-point length unit every authoring
/// validator and CHECK counts (<see cref="AuthoringText"/>). No database: the helper is pure.
/// </summary>
/// <remarks>
/// <para>
/// AC14 requires the write path to store composed text, so a decomposed Vietnamese input and its
/// composed form are one row, one <c>content_hash</c> and one authoring version. The other half of
/// the rule — that a JSON Schema <c>maxLength</c> and PostgreSQL <c>char_length</c> agree — is why
/// the length unit is code points and not UTF-16 units (13.2.1 §4 "char_length vs the UI counter").
/// </para>
/// <para>
/// The helper needs ICU: with <c>InvariantGlobalization=true</c> (00 §5.7 P7, which
/// <c>Evidgate.Api</c> and <c>Evidgate.Worker</c> set) .NET's <c>string.Normalize</c> is a no-op, so
/// <see cref="AuthoringText.Normalize"/> refuses instead of storing decomposed text. This suite
/// runs with ICU, so it pins the behaviour the API is supposed to have and records the host setting
/// the deployment has to reconcile (see the yield of 13.1.1-T1.2 and
/// <see cref="AuthoringText.NfcUnavailableMessage"/>).
/// </para>
/// </remarks>
public sealed partial class AuthoringUnitTests
{
    /// <summary>Composed Vietnamese "Tiếng Việt" (U+1EBF, U+1EC7) — the form a row must hold.</summary>
    private const string ComposedVietnamese = "Ti\u1ebfng Vi\u1ec7t";

    /// <summary>
    /// The same text decomposed the way an IME, a PDF extractor or a macOS pasteboard can emit it:
    /// <c>e</c> + U+0302 (above) + U+0301 (above) for <c>ế</c>, and <c>e</c> + U+0323 (below) +
    /// U+0302 (above) for <c>ệ</c>.
    /// </summary>
    private const string DecomposedVietnamese = "Tie\u0302\u0301ng Vie\u0323\u0302t";

    [Fact]
    public void TextNormalisation_IsNfc()
    {
        AuthoringText.IsNfcSupported.ShouldBeTrue(
            "the test host runs with ICU; if this is false the suite itself lost globalization");

        // Null-safe: an absent column value stays absent, and empty text is not an error.
        AuthoringText.Normalize(null).ShouldBeNull();
        AuthoringText.Normalize(string.Empty).ShouldBe(string.Empty);

        // Decomposed -> composed, the shortest case: e + U+0301 is U+00E9.
        var shortest = AuthoringText.Normalize("e\u0301").ShouldNotBeNull("NFC returns a value for non-null input");
        shortest.ShouldBe("\u00e9", "NFC composes a canonical pair");
        shortest.ShouldNotContain("\u0301", Case.Sensitive);

        // A real value: the composed row and the decomposed input must be the same string, which is
        // what makes them one version and one content_hash (AC14).
        AuthoringText.Normalize(DecomposedVietnamese).ShouldBe(ComposedVietnamese);
        AuthoringText.Normalize(ComposedVietnamese).ShouldBe(ComposedVietnamese, "already-NFC text is unchanged");

        // Idempotent, as NFC is by definition: composing a stored value again changes nothing.
        var once = AuthoringText.Normalize(DecomposedVietnamese);
        AuthoringText.Normalize(once).ShouldBe(once);

        // Composition order, not the caller's key order: below-then-above and above-then-below both
        // reach the same composed form, so a reordered combining sequence is not a second version.
        AuthoringText.Normalize("e\u0301\u0323").ShouldBe(AuthoringText.Normalize("e\u0323\u0301"));

        // Not culture-dependent: normalization is not collation, so the current culture cannot change
        // the bytes (the point 13.2.1 §4 asks this probe to prove).
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "vi-VN", "tr-TR", "ar-SA", CultureInfo.InvariantCulture.Name })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                AuthoringText.Normalize(DecomposedVietnamese).ShouldBe(ComposedVietnamese, culture);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        // Surrogates are preserved, never split or doubled: an emoji is one code point, one rune.
        AuthoringText.Normalize("😀").ShouldBe("😀");
        AuthoringText.Normalize("a😀e\u0301").ShouldBe("a😀\u00e9");

        // The length unit the validators and the CHECK share is the code point (EnumerateRunes):
        // char_length agrees, UTF-16 Length does not.
        AuthoringText.CodePointLength("e\u0301").ShouldBe(2, "a decomposed pair is two code points");
        AuthoringText.CodePointLength("\u00e9").ShouldBe(1, "the composed form is one");
        AuthoringText.CodePointLength("😀").ShouldBe(1, "one surrogate pair is one code point");
        AuthoringText.CodePointLength("🇻🇳").ShouldBe(2, "a flag built from two code points counts 2, as in char_length");
        AuthoringText.CodePointLength("😀").ShouldNotBe("😀".Length);
        AuthoringText.CodePointLength(null).ShouldBe(0);
        AuthoringText.CodePointLength(string.Empty).ShouldBe(0);
        AuthoringText.CodePointLength("\uD800").ShouldBe(1, "a lone surrogate still occupies the unit it is stored as");

        // The bounds of the steps schema (1..100 steps, 2000 characters per field, plan 13.1.1 §3) and
        // of the state-machine reason (1..500, AC20/AC21) are counted in that unit.
        AuthoringText.Fits(new string('a', 2000), 2000).ShouldBeTrue();
        AuthoringText.Fits(new string('a', 2001), 2000).ShouldBeFalse();
        AuthoringText.Fits(new string('ă', 2000), 2000).ShouldBeTrue("a 2-byte code point still counts 1");
        AuthoringText.Fits(string.Concat(Enumerable.Repeat("😀", 2000)), 2000)
            .ShouldBeTrue("2000 surrogate pairs are 2000 code points, not 4000 UTF-16 units");

        // Nothing to refuse in a process that can normalize.
        Should.NotThrow(AuthoringText.EnsureNfcSupported);
    }

    /// <summary>
    /// The P7 half of the probe: <c>Evidgate.Api</c> ships with <c>InvariantGlobalization=true</c>,
    /// where <c>string.Normalize(FormC)</c> returns its input unchanged — measured on .NET 10 with
    /// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c>: <c>"e\u0301"</c> stays two code points. A silent
    /// no-op would store decomposed text and break AC14 while every test passed, so
    /// <see cref="AuthoringText.Normalize"/> refuses with a message naming the setting instead.
    /// This test pins that the refusal exists and names the conflict; the reconciliation itself (keep
    /// ICU for the authoring hosts, or record that NFC is deferred) is a P7 decision, not a code one.
    /// </summary>
    [Fact]
    public void TextNormalisation_InvariantHostIsRefusedNotIgnored()
    {
        AuthoringText.NfcUnavailableMessage.ShouldContain("InvariantGlobalization");
        AuthoringText.NfcUnavailableMessage.ShouldContain("NFC");
        AuthoringText.NfcUnavailableMessage.ShouldContain("13.1.1", Case.Insensitive);

        // The setting the shipped host really runs with is declared in the build, so the premise is read
        // from there rather than from a copied runtimeconfig.json: one is the source of truth and cannot
        // drift with output layout, the other is a build artifact whose presence depends on packaging.
        var props = Path.Combine(RepositoryRoot(), "Directory.Build.props");
        File.Exists(props).ShouldBeTrue($"'{props}' is the source of the P7 process settings");

        var invariant = XDocument.Load(props).Descendants("PropertyGroup")
            .Where(group => ((string?)group.Attribute("Condition"))?.Contains("Evidgate.Api", StringComparison.Ordinal) == true)
            .SelectMany(group => group.Elements("InvariantGlobalization"))
            .Any(element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));

        // Whether the Api still declares invariant is the owner's P7 call; either way the helper must
        // not pretend. Measured: invariant mode makes Normalize a no-op, which is why the refusal path
        // exists at all — so while the Api is invariant, the refusal is the only correct behaviour and
        // the gap cannot be seen from this (ICU) suite, which is the point of the probe.
        if (invariant)
        {
            AuthoringText.IsNfcSupported.ShouldBeTrue(
                "this suite has ICU while the Api does not; if the suite itself were invariant the "
                + "production gap would be invisible rather than documented");
        }
        else
        {
            // The reconciliation landed: the Api keeps ICU, so Normalize composes in production too.
            AuthoringText.NfcUnavailableMessage.ShouldNotBeNullOrWhiteSpace();
        }
    }

    /// <summary>The repository root, found from the test output upwards by its solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
