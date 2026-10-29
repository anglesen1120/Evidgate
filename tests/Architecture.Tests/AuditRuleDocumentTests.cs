using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// AC9 (<c>11.9.2 §2</c>): <c>docs/security/audit-and-deletion-rule.md</c> is the written audit/deletion rule, and
/// this lint fails when one of its required sections is missing, duplicated or empty — a rule page cannot be
/// shortened away silently, because a section that vanishes takes its claim with it (no personal data in a chain
/// payload, the pseudonymous actor, the erasure tombstone, the free-text ban, the counsel question, the SQL writer and
/// platform-table position).
/// </summary>
/// <remarks>
/// <para>
/// The checker is a pure function over markdown text, so every rule is first proven on a synthetic document that
/// violates it (a required heading removed, one left without a body) and then run over the real page — the shape
/// <see cref="CatalogueTests"/> uses. The real-page test also asserts the page carries more than its headings, so a
/// document reduced to a table of contents fails instead of passing vacuously.
/// </para>
/// <para>
/// The check runs inside the existing <c>test</c> stage: <c>scripts/verify.sh --stage test</c> runs
/// <c>dotnet test --solution Evidgate.slnx</c>, and this class is part of that solution. No verify stage, <c>check-*</c>
/// script or CI workflow was changed for it.
/// </para>
/// </remarks>
public sealed class AuditRuleDocumentTests
{
    /// <summary>The rule page this lint guards, repo-relative with '/'.</summary>
    internal const string DocumentPath = "docs/security/audit-and-deletion-rule.md";

    /// <summary>
    /// The sections the page must keep, matched against the heading text of a top-level (<c>## </c>) heading. The
    /// numbering is part of the heading, so a reordered page is a failing page: the evidence table in §1 and the
    /// enforcement map in §9 refer to these numbers.
    /// </summary>
    internal static readonly string[] RequiredSections =
    [
        "1. Scope and status",
        "2. The actor is a pseudonymous `subject_id`",
        "3. Chain payloads carry no personal data",
        "4. Forbidden keys and how they are matched",
        "5. Erasure is a tombstone, not a row deletion",
        "6. Free text is forbidden in audit payloads",
        "7. Does a pseudonymous id stay personal data? `[OPEN]`",
        "8. SQL-side definer writers and the platform table",
        "9. Enforcement map and claim summary",
    ];

    /// <summary>A page holding only headings and one blank line would satisfy a heading-only lint.</summary>
    private const int MinimumDocumentChars = 4000;

    /// <summary>One row per required section, so a failure names the section that was removed.</summary>
    public static TheoryData<string> RequiredSectionNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var section in RequiredSections)
            {
                data.Add(section);
            }

            return data;
        }
    }

    /// <summary>AC9: the real page exists and keeps every required section, exactly once, each with a body.</summary>
    [Fact]
    public void AuditRule_Doc_RequiredSections()
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, DocumentPath);
        File.Exists(path).ShouldBeTrue($"{DocumentPath} does not exist");

        var markdown = File.ReadAllText(path);

        MarkdownHeadings(markdown).ShouldNotBeEmpty($"{DocumentPath} has no '## ' heading");
        markdown.Length.ShouldBeGreaterThan(
            MinimumDocumentChars,
            $"{DocumentPath} carries less than {MinimumDocumentChars} characters — the rule's prose is missing");

        var missing = SectionsMissing(markdown);
        missing.ShouldBeEmpty($"{DocumentPath} is missing the required section(s): {string.Join(", ", missing)}");

        var empty = SectionsWithoutBody(markdown);
        empty.ShouldBeEmpty($"{DocumentPath} leaves a required section without prose: {string.Join(", ", empty)}");

        foreach (var section in RequiredSections)
        {
            MarkdownHeadings(markdown).Count(heading => string.Equals(heading, section, StringComparison.Ordinal))
                .ShouldBe(1, $"{DocumentPath} must carry exactly one '## {section}' heading");
        }
    }

    /// <summary>Proof of the failure mode: removing any one required section is detected, and only that one.</summary>
    /// <param name="removed">The section the synthetic page leaves out.</param>
    [Theory]
    [MemberData(nameof(RequiredSectionNames))]
    public void Synthetic_Doc_Missing_A_Required_Section_Is_Rejected(string removed)
    {
        var document = SyntheticDocument(RequiredSections.Where(section => section != removed));

        SectionsMissing(document).ShouldHaveSingleItem().ShouldBe(removed);
    }

    /// <summary>A heading without prose is not a section: the synthetic page loses the body, not the heading.</summary>
    [Fact]
    public void Synthetic_Doc_With_An_Empty_Required_Section_Is_Rejected()
    {
        var documented = string.Join(
            "\n\n",
            RequiredSections.Select(section =>
                string.Equals(section, RequiredSections[4], StringComparison.Ordinal)
                    ? $"## {section}\n"
                    : $"## {section}\n\nBody."));

        var document = $"# Synthetic rule\n\n{documented}\n";

        SectionsMissing(document).ShouldBeEmpty();
        SectionsWithoutBody(document).ShouldHaveSingleItem().ShouldBe(RequiredSections[4]);
    }

    /// <summary>Positive control: the checker is not a constant, so a complete page passes with no finding.</summary>
    [Fact]
    public void Synthetic_Doc_With_Every_Required_Section_Passes()
    {
        var document = SyntheticDocument(RequiredSections);

        SectionsMissing(document).ShouldBeEmpty();
        SectionsWithoutBody(document).ShouldBeEmpty();
    }

    /// <summary>Builds a page with one body line per heading, in the given order.</summary>
    /// <param name="sections">The heading texts to emit, without the <c>##</c> prefix.</param>
    /// <returns>Markdown text.</returns>
    private static string SyntheticDocument(IEnumerable<string> sections)
    {
        var body = string.Join("\n\n", sections.Select(section => $"## {section}\n\nBody line."));
        return $"# Synthetic rule\n\n{body}\n";
    }

    /// <summary>The text of every top-level (<c>## </c>) heading in the document, trimmed.</summary>
    /// <param name="markdown">Markdown text.</param>
    /// <returns>Heading texts without the prefix, in source order.</returns>
    private static List<string> MarkdownHeadings(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return
        [
            .. Lines(markdown)
                .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
                .Select(line => line[3..].Trim()),
        ];
    }

    /// <summary>The required sections that have no heading in the document.</summary>
    /// <param name="markdown">Markdown text.</param>
    /// <returns>Missing heading texts, in required order.</returns>
    private static List<string> SectionsMissing(string markdown)
    {
        var present = MarkdownHeadings(markdown).ToHashSet(StringComparer.Ordinal);
        return [.. RequiredSections.Where(section => !present.Contains(section))];
    }

    /// <summary>The required sections whose heading is followed by no prose before the next heading.</summary>
    /// <param name="markdown">Markdown text.</param>
    /// <returns>Empty heading texts, in source order.</returns>
    private static List<string> SectionsWithoutBody(string markdown)
    {
        var lines = Lines(markdown);
        var empty = new List<string>();

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = lines[index][3..].Trim();
            if (!RequiredSections.Contains(heading, StringComparer.Ordinal))
            {
                continue;
            }

            var hasBody = false;
            for (var next = index + 1; next < lines.Count && !lines[next].StartsWith("## ", StringComparison.Ordinal); next++)
            {
                if (!string.IsNullOrWhiteSpace(lines[next]))
                {
                    hasBody = true;
                    break;
                }
            }

            if (!hasBody)
            {
                empty.Add(heading);
            }
        }

        return empty;
    }

    /// <summary>The document split into lines with <c>CRLF</c> and <c>CR</c> folded to <c>LF</c>.</summary>
    /// <param name="markdown">Markdown text.</param>
    /// <returns>The lines, without their terminators.</returns>
    private static List<string> Lines(string markdown) =>
        [.. markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')];
}
