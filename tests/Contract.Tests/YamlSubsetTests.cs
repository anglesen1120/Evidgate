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
    /// A flow mapping — the form the spec's page schemas use for <c>items: { $ref: … }</c> — parses, quoted values
    /// and nested flow collections included, so a page schema can be read like any other.
    /// </summary>
    [Fact]
    public void Parse_FlowMapping_Parses()
    {
        var node = YamlSubset.Parse(
            "items: { $ref: '#/components/schemas/EvidenceRevision' }\n" +
            "next_cursor: { type: [string, 'null'] }\n") as JsonObject;

        node.ShouldNotBeNull();
        node["items"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/schemas/EvidenceRevision");
        var types = node["next_cursor"]!["type"]!.AsArray();
        types.Count.ShouldBe(2);
        types[0]!.GetValue<string>().ShouldBe("string");
        types[1]!.GetValue<string>().ShouldBe("null");
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

    /// <summary>An apostrophe inside a plain scalar is literal text, in a mapping value, a flow item and a sequence item.</summary>
    [Fact]
    public void Parse_ApostropheInPlainScalar_IsLiteral()
    {
        var node = YamlSubset.Parse(
            "summary: Returns the caller's runs # note\n" +
            "tags: [don't, 'it''s']\n" +
            "items:\n" +
            "  - it's fine\n") as JsonObject;

        node.ShouldNotBeNull();
        node["summary"]!.GetValue<string>().ShouldBe("Returns the caller's runs");
        node["tags"]![0]!.GetValue<string>().ShouldBe("don't");
        node["tags"]![1]!.GetValue<string>().ShouldBe("it's");
        node["items"]![0]!.GetValue<string>().ShouldBe("it's fine");
    }

    /// <summary>A quote at the start of a value still opens a quoted scalar; left open it is refused.</summary>
    [Fact]
    public void Parse_QuoteOpenedScalarLeftOpen_Refuses()
    {
        var exception = Should.Throw<NotSupportedException>(() => YamlSubset.Parse("a: 1\nsummary: 'unterminated\n"));

        exception.Message.ShouldContain("line 2");
    }

    /// <summary>A flow sequence wrapped over several lines (Prettier output) reads like the single-line form, as a mapping value and a sequence item.</summary>
    [Fact]
    public void Parse_MultiLineFlowSequence_Parses()
    {
        var node = YamlSubset.Parse(
            "required: [id,\n" +
            "  'name', # why\n" +
            "\n" +
            "  status]\n" +
            "list:\n" +
            "  - [a,\n" +
            "    b]\n" +
            "after: 1\n") as JsonObject;

        node.ShouldNotBeNull();
        node["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["id", "name", "status"]);
        node["list"]![0]!.AsArray().Count.ShouldBe(2);
        node["after"]!.GetValue<long>().ShouldBe(1);
    }

    /// <summary>A flow list that never closes is refused with the line it opened on, not parsed to end of input.</summary>
    [Fact]
    public void Parse_UnterminatedMultiLineFlowSequence_Refuses()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => YamlSubset.Parse("required: [a,\n  b\nnext: 1\n"));

        exception.Message.ShouldContain("line 1");
        exception.Message.ShouldContain("never closed");
    }

    /// <summary>Prettier's layout for a long list: the bracket on its own line under the key, one item per line, trailing comma.</summary>
    [Fact]
    public void Parse_PrettierWrappedFlowList_Parses()
    {
        var node = YamlSubset.Parse(
            "schema:\n" +
            "  required:\n" +
            "    [\n" +
            "      id,\n" +
            "      name,\n" +
            "    ]\n" +
            "  type: object\n") as JsonObject;

        node.ShouldNotBeNull();
        node["schema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["id", "name"]);
        node["schema"]!["type"]!.GetValue<string>().ShouldBe("object");
    }

    /// <summary>Text after a closed quoted scalar is refused, not folded into the value.</summary>
    [Theory]
    [InlineData("a: 'x'y'\n")]
    [InlineData("a: \"x\"y\"\n")]
    public void Parse_TextAfterQuotedScalar_Refuses(string yaml)
        => Should.Throw<NotSupportedException>(() => YamlSubset.Parse(yaml)).Message.ShouldContain("line 1");

    /// <summary>A quote glued to a colon inside plain text is literal.</summary>
    [Fact]
    public void Parse_QuoteAfterColonInPlainScalar_IsLiteral()
    {
        var node = YamlSubset.Parse("summary: prefix:'suffix\n") as JsonObject;

        node.ShouldNotBeNull();
        node["summary"]!.GetValue<string>().ShouldBe("prefix:'suffix");
    }

    /// <summary>YAML 1.2 forbids an empty flow entry; only one trailing comma is optional, in a sequence and in a mapping.</summary>
    [Theory]
    [InlineData("a: [a,,]\n")]
    [InlineData("a: [,a]\n")]
    [InlineData("a: [a,,b]\n")]
    [InlineData("a: {x: 1,,}\n")]
    public void Parse_EmptyFlowEntry_Refuses(string yaml)
        => Should.Throw<NotSupportedException>(() => YamlSubset.Parse(yaml)).Message.ShouldContain("empty entry");

    /// <summary>One trailing comma in a flow mapping is dropped, like in a flow sequence.</summary>
    [Fact]
    public void Parse_FlowMappingTrailingComma_Parses()
    {
        var node = YamlSubset.Parse("a: {x: 1, y: two,}\n") as JsonObject;

        node.ShouldNotBeNull();
        node["a"]!.AsObject().Count.ShouldBe(2);
        node["a"]!["y"]!.GetValue<string>().ShouldBe("two");
    }

    /// <summary>A tab between a flow indicator and a quote still opens the quoted scalar, so a comma inside it is data.</summary>
    [Fact]
    public void Parse_TabBeforeQuote_OpensQuotedScalar()
    {
        var node = YamlSubset.Parse("a: [\t'x, y',\t\"z, w\"]\nb: {k:\t'p, q'}\n") as JsonObject;

        node.ShouldNotBeNull();
        node["a"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["x, y", "z, w"]);
        node["b"]!["k"]!.GetValue<string>().ShouldBe("p, q");
    }

    /// <summary>
    /// A list left open is joined up to the next bracket, as YAML's multi-line flow rule requires. The reader keeps
    /// the later lines as plain text entries (it does not model implicit pairs), so they are never separate mapping members.
    /// </summary>
    [Fact]
    public void Parse_UnterminatedListFollowedByLaterBracket_JoinsUpToThatBracket()
    {
        var node = YamlSubset.Parse("a: [x,\nb: y\nc: z]\n") as JsonObject;

        node.ShouldNotBeNull();
        node.Count.ShouldBe(1);
        node["a"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["x", "b: y c: z"]);
    }

    /// <summary>A very long wrapped list parses to every item (guards the incremental depth scan against dropping or double-counting lines).</summary>
    [Fact]
    public void Parse_LongWrappedFlowList_KeepsEveryItem()
    {
        var lines = string.Join(",\n", Enumerable.Range(0, 5000).Select(n => "  'it'" + "'" + "s " + n + "'"));
        var node = YamlSubset.Parse("a: [\n" + lines + ",\n]\nafter: 1\n") as JsonObject;

        node.ShouldNotBeNull();
        node["a"]!.AsArray().Count.ShouldBe(5000);
        node["a"]![0]!.GetValue<string>().ShouldBe("it's 0");
        node["a"]![4999]!.GetValue<string>().ShouldBe("it's 4999");
        node["after"]!.GetValue<long>().ShouldBe(1);
    }
}
