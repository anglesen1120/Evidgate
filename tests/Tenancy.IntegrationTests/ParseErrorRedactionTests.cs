using System.Reflection;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1.1 (00 §5.7 S3; plan §8): a parse refusal carries the registered code, a fixed message and a
/// generic reason — never an artifact byte, an element name, a key or a path. The canary is a plain word
/// planted in the hostile body (never a secret-shaped literal), so a leak is unambiguous.
/// </summary>
public sealed class ParseErrorRedactionTests
{
    /// <summary>A plain word planted in every hostile body; it must never appear in a failure.</summary>
    private const string Canary = "canaryword";

    /// <summary>The five generic reasons 06.3.1 §2 allows, and nothing else.</summary>
    private static readonly string[] ClosedReasons =
    [
        ParsingFailureReason.DtdProhibited,
        ParsingFailureReason.DepthExceeded,
        ParsingFailureReason.SizeExceeded,
        ParsingFailureReason.Timeout,
        ParsingFailureReason.Malformed,
        ParsingFailureReason.EntryCountExceeded,
        ParsingFailureReason.CompressionRatioExceeded,
        ParsingFailureReason.NestingExceeded,
        ParsingFailureReason.DuplicateEntry,
        ParsingFailureReason.UnsafeEntryName,
    ];

    public static TheoryData<string, long, string> HostileXml => new()
    {
        { DtdDocument(), 4096, ParsingFailureReason.DtdProhibited },
        { $"<{Canary}>", 4096, ParsingFailureReason.Malformed },
        { $"<{Canary} attr=\"unclosed>", 4096, ParsingFailureReason.Malformed },
        { NestedDocument(65, Canary), 4096, ParsingFailureReason.DepthExceeded },
        { OpaqueElement(Canary, 2048), 1024, ParsingFailureReason.SizeExceeded },
        { $"<!-- {Canary} -->", 4096, ParsingFailureReason.Malformed },
    };

    public static TheoryData<string, long, string> HostileJson => new()
    {
        { $"{{\"{Canary}\": }}", 4096, ParsingFailureReason.Malformed },
        { $"{{\"{Canary}\":", 4096, ParsingFailureReason.Malformed },
        { NestedJson(65, Canary), 4096, ParsingFailureReason.DepthExceeded },
        { OpaqueJson(Canary, 2048), 1024, ParsingFailureReason.SizeExceeded },
        { $"[\"{Canary}\",", 4096, ParsingFailureReason.Malformed },
    };

    [Theory]
    [MemberData(nameof(HostileXml))]
    public void XmlFailuresCarryNoArtifactContent(string payload, long maxBytes, string expectedReason)
    {
        var result = SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

        AssertRedacted(result, expectedReason);
    }

    [Theory]
    [MemberData(nameof(HostileJson))]
    public void JsonFailuresCarryNoArtifactContent(string payload, long maxBytes, string expectedReason)
    {
        var result = SafeJson.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

        AssertRedacted(result, expectedReason);
    }

    [Fact]
    public void MessagesAreConstantsNotEchoesOfTheInput()
    {
        // Two different malformed documents with different element names produce the same message, so the
        // message cannot be carrying any part of the input.
        var first = SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<alpha>")), 1024, CancellationToken.None);
        var second = SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<beta>")), 1024, CancellationToken.None);

        first.Error!.Message.ShouldBe(second.Error!.Message);
        first.Error.Message.ShouldNotContain("alpha");
        second.Error.Message.ShouldNotContain("beta");

        // The same holds across the two profiles for the same reason.
        var json = SafeJson.Parse(new MemoryStream(Encoding.UTF8.GetBytes("{")), 1024, CancellationToken.None);
        json.Error!.Message.ShouldBe(first.Error.Message);
    }

    [Fact]
    public void FailureCarriesTheRegisteredCodesAndStatuses()
    {
        // 00 §9.2: 422 artifact_invalid is the registered parse-profile code; the size cap maps to the
        // registered 413 payload_too_large (06.1.2-T1). Neither is retryable, and both carry exactly one
        // detail naming the reason.
        var invalid = SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<r>")), 1024, CancellationToken.None);

        invalid.Error!.Code.ShouldBe(ApiErrorCode.ArtifactInvalid);
        invalid.Error.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        invalid.Error.Status.ShouldBe(422);
        invalid.Error.Retryable.ShouldBeFalse();
        invalid.Error.Details!.Count.ShouldBe(1);
        invalid.Error.Details[0].Path.ShouldBe("content");

        var tooLarge = SafeXml.Parse(
            new MemoryStream(Encoding.UTF8.GetBytes(OpaqueElement("x", 2048))), 1024, CancellationToken.None);

        tooLarge.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        tooLarge.Error.Code.ShouldBe(ApiErrorCode.PayloadTooLarge);
        tooLarge.Error.Status.ShouldBe(413);
        tooLarge.Error.Retryable.ShouldBeFalse();
        tooLarge.Error.Details!.Single().Reason.ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void ReasonsAreAClosedSet()
    {
        var declared = typeof(ParsingFailureReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        declared.ShouldBe(ClosedReasons.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Asserts the failure is the expected generic reason and carries no canary anywhere.</summary>
    private static void AssertRedacted(Result result, string expectedReason)
    {
        result.IsSuccess.ShouldBeFalse();

        var error = result.Error!;
        var details = error.Details!;

        details.Single().Reason.ShouldBe(expectedReason);
        error.Code.ShouldBeOneOf(ParsingErrorCode.ArtifactInvalid, ParsingErrorCode.PayloadTooLarge);

        var rendered = string.Join(
            '\u0000',
            new[] { error.Code, error.Message }
                .Concat(details.Select(detail => detail.Path))
                .Concat(details.Select(detail => detail.Reason)));

        rendered.ShouldNotContain(Canary, Case.Insensitive);
        rendered.ShouldNotContain("canary", Case.Insensitive);
        rendered.ShouldNotContain("file:", Case.Insensitive);
        rendered.ShouldNotContain("/", Case.Sensitive);
        rendered.ShouldNotContain("<", Case.Sensitive);

        // The reason is one of the five generic words, never a message built from the document.
        expectedReason.ShouldBeOneOf(ClosedReasons);
    }

    /// <summary>A DTD document whose names all carry the canary.</summary>
    private static string DtdDocument() =>
        $"<!DOCTYPE {Canary} [<!ENTITY {Canary} SYSTEM \"file:///etc/{Canary}\">]><{Canary}>&{Canary};</{Canary}>";

    /// <summary>A document nested <paramref name="levels"/> deep, every element named <paramref name="name"/>.</summary>
    private static string NestedDocument(int levels, string name) =>
        string.Concat(Enumerable.Repeat($"<{name}>", levels))
        + string.Concat(Enumerable.Repeat($"</{name}>", levels));

    /// <summary>A well-formed document of roughly <paramref name="bytes"/> bytes holding the canary as text.</summary>
    private static string OpaqueElement(string name, int bytes) =>
        $"<{name}>" + new string('x', Math.Max(0, bytes - name.Length * 2 - 5)) + $"</{name}>";

    /// <summary>Nested JSON objects <paramref name="levels"/> deep, every key the canary.</summary>
    private static string NestedJson(int levels, string key) =>
        string.Concat(Enumerable.Repeat($"{{\"{key}\":", levels)) + "1" + new string('}', levels);

    /// <summary>A well-formed JSON payload of roughly <paramref name="bytes"/> bytes holding the canary.</summary>
    private static string OpaqueJson(string key, int bytes) =>
        $"{{\"{key}\":\"" + new string('x', Math.Max(0, bytes - key.Length - 8)) + "\"}";
}
