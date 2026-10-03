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
    private static IReadOnlyList<string> Assertions(string framework, string source, string? language = null) => AssertionExtractor.Extract(framework, source, language).Descriptors;

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
        Assertions("jest", "expect(a).toBe(").ShouldBeEmpty();
        AssertionExtractor.Extract("jest", "expect(a).toBe(").Reliable.ShouldBeFalse();
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

    [Fact]
    public void Script_AssertionsInsideASubject_AreExtracted()
    {
        const string source = "await expect(async () => { expect(a).toBe(1); expect(b).not.toBeNull(); }).toPass();";

        Assertions("playwright", source).ShouldBe(["in:expect:::toBe:1", "in:expect::not:toBeNull:", "expect:::toPass:"]);
    }

    [Fact]
    public void Script_ChaiChains_KeepTheMeaningfulWordsAndArguments()
    {
        Assertions("mocha", "expect(a).to.be.deep.equal({ x: 1 });").ShouldBe(["expect:::equal:{ x: 1 }"]);
        Assertions("mocha", "expect(a).to.equal(1);").ShouldNotBe(Assertions("mocha", "expect(a).to.equal(2);"));
        Assertions("mocha", "expect(a).to.be.true;").ShouldNotBe(Assertions("mocha", "expect(a).to.be.false;"));
    }

    [Fact]
    public void Script_NodeAndChaiAssertCalls_AreExtracted()
    {
        Assertions("mocha", "assert.strictEqual(a, 1); assert(ok);").ShouldBe(["assert:strictEqual:a, 1", "assert::ok"]);
    }

    [Fact]
    public void Script_ALineCommentInArguments_CannotSwallowWhatFollows()
    {
        var withBreak = Assertions("jest", "expect(a).toEqual(\n  x, // note\n  y\n);").Single();
        var swallowed = Assertions("jest", "expect(a).toEqual(\n  x, // note y\n);").Single();

        withBreak.ShouldNotBe(swallowed);
    }

    [Theory]
    [InlineData("const re = /[\"']/; expect(a).toBe(1);")]
    [InlineData("const s = 'unterminated;\nexpect(a).toBe(1);")]
    [InlineData("/* never closed\nexpect(a).toBe(1);")]
    [InlineData("expect(a).toBe(")]
    public void Script_WhatTheScannerCannotFollow_IsMarkedUnreliable(string source)
    {
        AssertionExtractor.Extract("jest", source).Reliable.ShouldBeFalse();
    }

    [Fact]
    public void Script_ACleanFile_IsReliable_AndAFileWithoutAssertionsToo()
    {
        AssertionExtractor.Extract("jest", "expect(a).toBe(1);").Reliable.ShouldBeTrue();
        AssertionExtractor.Extract("jest", "const x = 1;").Reliable.ShouldBeTrue();
        AssertionExtractor.Extract("jest", "const x = 1;").Descriptors.ShouldBeEmpty();
    }

    [Fact]
    public void Python_CommentsInsideAMultiLineAssert_DoNotHideTheClausesAfterThem()
    {
        const string source = "assert (\n  r.status == 200  # ok\n  and body['x'] == 1\n)\n";

        Assertions("pytest", source).Single().ShouldContain("body['x'] == 1");
    }

    [Fact]
    public void Python_OneLineBlocks_AndPlaywrightExpect_AreAssertions()
    {
        Assertions("pytest", "if ready: assert value == 1\nexpect(page).to_have_title('Home')\n").ShouldBe(
            ["assert::value == 1", "expect:::to_have_title:'Home'"]);
        Assertions("playwright", "assert x == 2\n", "python").ShouldBe(["assert::x == 2"]);
    }

    [Fact]
    public void FailClosed_UnsupportedAndUnreliableFiles_GetAHashThatChangesWithAnyEdit()
    {
        string Hash(string framework, string source) => RevisionHasher.Describe(framework, "x", source).AssertionHash;

        // Unsupported framework: editing the file changes the assertion hash (it is not a constant).
        Hash("junit", "assertEquals(1, a);").ShouldNotBe(Hash("junit", "// removed"));
        Hash("junit", "assertEquals(1, a);").ShouldBe(Hash("junit", "assertEquals(1, a);"));

        // Unreliable scan: same.
        Hash("jest", "const s = 'oops;\nexpect(a).toBe(1);").ShouldNotBe(Hash("jest", "const s = 'oops;\n"));

        // A supported, understood file keeps the descriptor-only hash (a comment edit leaves it alone).
        Hash("jest", "expect(a).toBe(1);").ShouldBe(Hash("jest", "expect(a).toBe(1); // new comment"));

        RevisionHasher.Describe("junit", null, "x").MetadataJson.ShouldContain("\"assertions_reliable\":false");
        RevisionHasher.Describe("jest", null, "expect(a).toBe(1);").MetadataJson.ShouldContain("\"assertions_reliable\":true");
    }

    [Fact]
    public void TextRules_IsWellFormed_AllowsSourceCodeCharacters_ButNotLoneSurrogates()
    {
        TextRules.IsWellFormed("tab\tand\nnewline and 😀").ShouldBeTrue();
        TextRules.IsWellFormed("x" + (char)0xD800).ShouldBeFalse();
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
            {"metadata":{"assertions_reliable":true,"assertions_extracted":true,"assertion_count":1},"language":"typescript","framework":"playwright","assertion_hash":"{{first.AssertionHash}}","content_sha256":"{{first.ContentSha256}}"}
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
