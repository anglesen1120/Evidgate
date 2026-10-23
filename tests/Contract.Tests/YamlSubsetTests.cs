using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// <see cref="YamlSubset"/> refusals that no spec-dependent suite pins: the
/// reader is deliberately strict (its doc promises it throws instead of
/// guessing), so these tests pin the refusal behaviours that would otherwise
/// only surface inside a confusing contract-test failure — above all, a
/// duplicate mapping key, which YAML forbids and a silent last-wins would hide.
/// </summary>
public sealed class YamlSubsetTests
{
    /// <summary>A duplicated key in the root mapping is refused, not collapsed.</summary>
    [Fact]
    public void Parse_DuplicateRootKey_Refuses()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => YamlSubset.Parse("required: [a]\nrequired: [b]\n"));

        exception.Message.ShouldContain("duplicate mapping key 'required'");
        exception.Message.ShouldContain("line 2");
    }

    /// <summary>A duplicated key inside a nested mapping is refused too.</summary>
    [Fact]
    public void Parse_DuplicateNestedKey_Refuses()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => YamlSubset.Parse("Foo:\n  required: [a]\n  required: [b]\n"));

        exception.Message.ShouldContain("duplicate mapping key 'required'");
        exception.Message.ShouldContain("line 3");
    }

    /// <summary>
    /// A <c>- key: value</c> item degrades into a mapping; a duplicated key
    /// inside one item is refused like any other mapping duplicate.
    /// </summary>
    [Fact]
    public void Parse_DuplicateKeyInSequenceItem_Refuses()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => YamlSubset.Parse("- name: a\n  value: 1\n  value: 2\n"));

        exception.Message.ShouldContain("duplicate mapping key 'value'");
    }

    /// <summary>
    /// Quoted spellings are decoded before the duplicate check: <c>'a'</c> and
    /// <c>a</c> are the same key.
    /// </summary>
    [Fact]
    public void Parse_DuplicateQuotedKey_Refuses()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => YamlSubset.Parse("key: 1\n'key': 2\n"));

        exception.Message.ShouldContain("duplicate mapping key 'key'");
    }

    /// <summary>
    /// No false positives: sibling keys at every depth and a key repeated in a
    /// <em>different</em> mapping are all still legal — only a repeat inside the
    /// same mapping is refused.
    /// </summary>
    [Fact]
    public void Parse_DistinctMappings_StillParse()
    {
        var node = YamlSubset.Parse(
            "openapi: 3.1.0\n" +
            "components:\n" +
            "  schemas:\n" +
            "    A:\n" +
            "      type: object\n" +
            "      required: [name]\n" +
            "    B:\n" +
            "      type: string\n" +
            "      required: [other]\n") as JsonObject;

        node.ShouldNotBeNull();
        var a = node["components"]!["schemas"]!["A"]!;
        var b = node["components"]!["schemas"]!["B"]!;
        a["type"]!.GetValue<string>().ShouldBe("object");
        b["type"]!.GetValue<string>().ShouldBe("string");
    }
}
