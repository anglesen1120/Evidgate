using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 03.1.3-T1.2 without a database: assertion extraction (AC4), revision hashing (AC1, AC3) and the text rules that
/// keep a customer's strings out of trouble.
/// </summary>
public sealed class CatalogRevisionUnitTests
{
    private static IReadOnlyList<string> Assertions(string framework, string source) => AssertionExtractor.Extract(framework, source).Descriptors;

    // ------------------------------------------------------------------ AssertionExtractor (script family)

    [Fact]
    public void Script_ExpectChains_AreReducedToMatcherNegationAndExpectedValue()
    {
        const string source = """
            test('login', async ({ page }) => {
              await expect(page.locator('#welcome')).toHaveText('Hello   world');
              await expect(page).not.toHaveURL(/login/);
              await expect.soft(page.getByRole('button')).toBeVisible();
              await expect.poll(async () => await count()).toBeGreaterThan(2);
              await expect(promise).resolves.toBe(1);
            });
            """;

        Assertions("playwright", source).ShouldBe(
        [
            "expect:::toHaveText:'Hello   world'",
            "expect::not:toHaveURL:/login/",
            "expect:soft::toBeVisible:",
            "expect:poll::toBeGreaterThan:2",
            "expect::|resolves:toBe:1",
        ]);
    }

    [Fact]
    public void Script_HealingALocator_DoesNotChangeTheAssertionHash_ButWeakeningDoes()
    {
        string Hash(string line) => RevisionHasher.Describe("playwright", "typescript", $"test('x', async () => {{ {line} }});").AssertionHash;

        var original = Hash("await expect(page.locator('#old')).toHaveText('Hello');");

        Hash("await expect(page.locator('#healed')).toHaveText('Hello');").ShouldBe(original);
        Hash("await expect(page.locator('#old')).toContainText('Hello');").ShouldNotBe(original);
        Hash("await expect(page.locator('#old')).toHaveText('Goodbye');").ShouldNotBe(original);
        Hash("await expect(page.locator('#old')).not.toHaveText('Hello');").ShouldNotBe(original);
        Hash(string.Empty).ShouldNotBe(original);
        Hash("// await expect(page.locator('#old')).toHaveText('Hello');").ShouldBe(Hash(string.Empty));
    }

    [Fact]
    public void Script_CommentsAndStringsAreNotAssertions()
    {
        const string source = """
            // await expect(a).toBe(1);
            /* expect(b).toBe(2) */
            const s = "expect(c).toBe(3)";
            const t = `expect(d).toBe(4)`;
            await expect(real).toBe(5);
            """;

        Assertions("jest", source).ShouldBe(["expect:::toBe:5"]);
    }

    [Fact]
    public void Script_FormattingDoesNotChangeADescriptor_ButStringContentDoes()
    {
        Assertions("vitest", "expect(a)\n  .toEqual(\n    { x:   1,\n y: 2 }\n  )").ShouldBe(Assertions("vitest", "expect(a).toEqual({ x: 1, y: 2 })"));
        Assertions("vitest", "expect(a).toBe('a  b')").ShouldNotBe(Assertions("vitest", "expect(a).toBe('a b')"));
    }

    [Fact]
    public void Script_Cypress_ShouldAndAndChains_AreAssertions()
    {
        Assertions("cypress", "cy.get('.a').should('have.text', 'Hi').and('be.visible');").ShouldBe(
            ["should::'have.text', 'Hi'", "and::'be.visible'"]);
    }

    [Fact]
    public void Script_UnbalancedOrPathologicalSource_NeverThrows_AndStaysBounded()
    {
        Assertions("jest", "expect(((((").ShouldBeEmpty();
        Assertions("jest", "expect(a).toBe(").ShouldBe(["expect:::toBe:"]);
        Assertions("jest", "expect").ShouldBeEmpty();
        Assertions("jest", "expect(a).").ShouldBeEmpty();
        Assertions("jest", "expect(a);").ShouldBe(["expect:::?:"]);

        var deep = "expect(" + new string('(', 500) + new string(')', 500) + ").toBe(1)";
        Assertions("jest", deep).ShouldBeEmpty();

        var many = string.Concat(Enumerable.Repeat("expect(a).toBe(1);\n", AssertionExtractor.MaxDescriptors + 100));
        Assertions("jest", many).Count.ShouldBe(AssertionExtractor.MaxDescriptors);

        var started = System.Diagnostics.Stopwatch.StartNew();
        _ = Assertions("jest", string.Concat(Enumerable.Repeat("expect(a)", 50_000)));
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Script_VeryLongArguments_AreReplacedByADigest_StillDistinguishingThem()
    {
        var first = Assertions("jest", $"expect(a).toBe('{new string('x', 2000)}')").Single();
        var second = Assertions("jest", $"expect(a).toBe('{new string('x', 1999)}y')").Single();

        first.ShouldContain(":h:");
        first.Length.ShouldBeLessThan(80);
        first.ShouldNotBe(second);
    }

    // ------------------------------------------------------------------ AssertionExtractor (python)

    [Fact]
    public void Python_AssertStatements_UnittestMethodsAndRaises()
    {
        const string source = """
            def test_a(client):
                # assert commented == out
                assert response.status_code == 200  # trailing
                assert x in [
                    1,
                    2,
                ]
                s = "assert not_this"
                self.assertEqual(a, b)
                with pytest.raises(ValueError):
                    pass
                value = 1; assert value
            """;

        Assertions("pytest", source).ShouldBe(
        [
            "assert::response.status_code == 200",
            "assert::x in [ 1, 2, ]",
            "assertEqual::a, b",
            "raises::ValueError",
            "assert::value",
        ]);
    }

    [Fact]
    public void UnknownFramework_YieldsAnEmptyFlaggedExtraction()
    {
        var result = AssertionExtractor.Extract("rspec", "expect(a).to eq(1)");

        (result.Extracted, result.Descriptors.Count).ShouldBe((false, 0));
        AssertionExtractor.Supports("rspec").ShouldBeFalse();
        AssertionExtractor.Supports("Playwright").ShouldBeTrue();
        RevisionHasher.Describe("rspec", null, "x").MetadataJson.ShouldContain("\"assertions_extracted\":false");
    }

    // ------------------------------------------------------------------ RevisionHasher

    [Fact]
    public void Hasher_IsDeterministic_AndMatchesTheSpecifiedCanonicalDocument()
    {
        var first = RevisionHasher.Describe("playwright", "typescript", "expect(a).toBe(1);");
        var again = RevisionHasher.Describe("playwright", "typescript", "expect(a).toBe(1);");

        again.ShouldBe(first);
        first.ContentSha256.ShouldStartWith("sha256:");
        first.RevisionHash.Length.ShouldBe(71);

        // The hash is the digest of exactly {content_sha256, framework, language, assertion_hash, metadata}.
        var document = $$"""
            {"metadata":{"assertions_extracted":true,"assertion_count":1},"language":"typescript","framework":"playwright","assertion_hash":"{{first.AssertionHash}}","content_sha256":"{{first.ContentSha256}}"}
            """;
        first.RevisionHash.ShouldBe(CanonicalHash.Compute(document));
    }

    [Fact]
    public void Hasher_EveryIdentityMemberMatters_ButTheDescriptorHasNoPath()
    {
        var baseline = RevisionHasher.Describe("playwright", "typescript", "expect(a).toBe(1);");

        RevisionHasher.Describe("playwright", "typescript", "expect(a).toBe(2);").RevisionHash.ShouldNotBe(baseline.RevisionHash);
        RevisionHasher.Describe("playwright", "typescript", "expect(a).toBe(1); ").RevisionHash.ShouldNotBe(baseline.RevisionHash);
        RevisionHasher.Describe("jest", "typescript", "expect(a).toBe(1);").RevisionHash.ShouldNotBe(baseline.RevisionHash);
        RevisionHasher.Describe("playwright", "javascript", "expect(a).toBe(1);").RevisionHash.ShouldNotBe(baseline.RevisionHash);
        RevisionHasher.Describe("playwright", null, "expect(a).toBe(1);").RevisionHash.ShouldNotBe(baseline.RevisionHash);

        typeof(RevisionDescriptor).GetProperties().Select(property => property.Name).ShouldNotContain("FilePath");
    }

    [Fact]
    public void Hasher_AnAssertionOnlyChange_ChangesBothHashes()
    {
        var before = RevisionHasher.Describe("jest", "javascript", "test('x', () => { expect(a).toBe(1); });");
        var after = RevisionHasher.Describe("jest", "javascript", "test('x', () => { expect(a).toBe(1); expect(b).toBe(2); });");

        after.AssertionHash.ShouldNotBe(before.AssertionHash);
        after.RevisionHash.ShouldNotBe(before.RevisionHash);
    }

    [Fact]
    public void Hasher_NeverReturnsTheSource()
    {
        var descriptor = RevisionHasher.Describe("playwright", "typescript", "const password = 'hunter2'; expect(a).toBe(1);");

        $"{descriptor.ContentSha256}{descriptor.AssertionHash}{descriptor.MetadataJson}{descriptor.RevisionHash}".ShouldNotContain("hunter2");
        descriptor.MetadataJson.Length.ShouldBeLessThan(100);
    }

    // ------------------------------------------------------------------ TextRules

    [Theory]
    [InlineData("plain name", true)]
    [InlineData("naïve ☃ 日本語 😀", true)]
    [InlineData("", true)]
    [InlineData("nul\0inside", false)]
    [InlineData("tab\there", false)]
    [InlineData("bidi‮override", false)]
    [InlineData("zero​width", false)]
    [InlineData("bom﻿mark", false)]
    public void TextRules_IsClean(string text, bool expected) => TextRules.IsClean(text).ShouldBe(expected);

    [Fact]
    public void TextRules_LoneSurrogates_AreNotClean()
    {
        // Built at run time: an attribute argument cannot carry an unpaired surrogate through metadata.
        TextRules.IsClean("lone" + (char)0xD800 + "high").ShouldBeFalse();
        TextRules.IsClean("lone" + (char)0xDC00 + "low").ShouldBeFalse();
        TextRules.IsClean("pair" + (char)0xD83D + (char)0xDE00).ShouldBeTrue();
    }

    [Fact]
    public void TextRules_NullIsNotClean() => TextRules.IsClean(null).ShouldBeFalse();

    [Fact]
    public void TextRules_Truncate_NeverSplitsASurrogatePair()
    {
        var text = "ab😀cd";

        TextRules.Truncate(text, 3).ShouldBe("ab");
        TextRules.Truncate(text, 4).ShouldBe("ab😀");
        TextRules.Truncate(text, 99).ShouldBe(text);
        TextRules.IsClean(TextRules.Truncate(string.Concat(Enumerable.Repeat("😀", 400)), 513)).ShouldBeTrue();
    }

    [Fact]
    public void TextRules_Brief_IsOneControlFreeLine()
    {
        TextRules.Brief("a\nb\0c‮d").ShouldBe("a?b?c?d");
        TextRules.Brief(new string('x', 500)).Length.ShouldBe(121);
        TextRules.IsClean(TextRules.Brief("😀😀😀", 3)).ShouldBeTrue();
    }

    [Fact]
    public void RepoPath_RefusesFormatAndMalformedCharacters()
    {
        RepoPath.IsValid("tests/a‮.ts").ShouldBeFalse();
        RepoPath.IsValid("tests/a\uD800.ts").ShouldBeFalse();
        RepoPath.IsValid("tests/日本語.spec.ts").ShouldBeTrue();
    }
}
