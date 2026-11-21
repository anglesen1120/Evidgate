using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EventBatchParserUnitTests
{
    // ---- closed member sets (additionalProperties: false) -----------------------------------

    [Theory]
    [InlineData("meta")]
    [InlineData("payload")]
    public void Unknown_Event_Member_Is_Refused_At_Its_Path(string name)
    {
        var result = Parse(Batch(EventPlus(name, "\"x\"")));

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/0/" + name);
        result.Errors[0].Reason.ShouldBe("unknown_member");
        result.Errors[0].Index.ShouldBe(0);
    }

    [Fact]
    public void Legacy_Kind_Member_Is_Refused_Like_Any_Unknown_Event_Member()
    {
        // The S33 `kind` -> `type` carrier fix: the closed envelope has no `kind`, so an old reporter
        // that still sends it is refused rather than silently accepted (and hashed).
        var result = Parse(Batch(EventPlus("kind", "\"test.started\"")));

        result.IsAccepted.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/0/kind");
        result.Errors[0].Reason.ShouldBe("unknown_member");
        result.Errors[0].Index.ShouldBe(0);
    }

    [Fact]
    public void Unknown_Batch_Member_Is_Refused_At_Its_Path()
    {
        var result = Parse("{\"schema_version\":1,\"kind\":\"batch\",\"events\":[" + ValidEvent() + "]}");

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/kind");
        result.Errors[0].Reason.ShouldBe("unknown_member");
        result.Errors[0].Index.ShouldBeNull();
    }

    // ---- hostile JSON -----------------------------------------------------------------------

    [Fact]
    public void Deeply_Nested_Body_Is_Malformed_Not_A_Stack_Overflow()
    {
        var depth = JsonCanonicalizer.MaxDepth + 1;
        var json = new string('[', depth) + new string(']', depth);

        var result = Parse(json);

        result.IsAccepted.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Reason.ShouldBe("malformed");
    }

    [Fact]
    public void Nul_And_Lone_Surrogate_Escapes_Are_Unstorable_Text()
    {
        var escapes = new[] { "\\u0000", "\\ud800" };

        foreach (var escape in escapes)
        {
            var result = Parse(Batch(EventWith("producer_id", "\"a" + escape + "b\"")));

            result.IsAccepted.ShouldBeFalse();
            result.Errors.Count.ShouldBe(1);
            result.Errors[0].Path.ShouldBe("/events/0");
            result.Errors[0].Reason.ShouldBe("invalid_value");
        }
    }

    [Fact]
    public void A_Document_The_Canonicalizer_Refuses_Is_Not_Canonicalisable()
    {
        // A number outside the IEEE 754 double range has no RFC 8785 form (inside an allow-listed ext
        // value, so no earlier rule reports it).
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":{"n":1e400}}""")));

        result.IsAccepted.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/0");
        result.Errors[0].Reason.ShouldBe("not_canonicalisable");
    }

    [Fact]
    public void Duplicate_Member_Names_Are_Refused()
    {
        var duplicate = """{"schema_version":1,"schema_version":1,"event_id":"<id>","producer_id":"producer-1","sequence":0,"execution_key":"<key>","attempt_no":1,"type":"test.started","occurred_at":"2026-10-13T10:00:00Z"}"""
            .Replace("<id>", EventId, StringComparison.Ordinal)
            .Replace("<key>", ExecutionKey, StringComparison.Ordinal);

        var result = Parse(Batch(duplicate));

        // Either the reader refuses the body or the canonicalizer refuses the duplicate name; the batch
        // is never accepted.
        result.IsAccepted.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Reason.ShouldBeOneOf("not_canonicalisable", "malformed");
    }

    // ---- payload hash -----------------------------------------------------------------------

    [Fact]
    public void Payload_Hash_Is_The_Shared_Canonical_Hash_Of_The_Received_Element()
    {
        var result = Parse(Batch(ValidEvent()));

        result.Events[0].PayloadHash.ShouldBe(CanonicalHash.Compute(result.Events[0].Element));
    }

    [Fact]
    public void Payload_Hash_Ignores_Member_Order_And_Formatting()
    {
        var forward = Event(
            ("schema_version", "1"),
            ("event_id", "\"" + EventId + "\""),
            ("producer_id", "\"producer-1\""),
            ("sequence", "0"),
            ("execution_key", "\"" + ExecutionKey + "\""),
            ("attempt_no", "1"),
            ("type", "\"test.started\""),
            ("occurred_at", "\"2026-10-13T10:00:00Z\""),
            ("ext", """{"schema":"playwright/1","title":"t"}"""));

        var reversed = Event(
            ("occurred_at", "\"2026-10-13T10:00:00Z\""),
            ("type", "\"test.started\""),
            ("attempt_no", "1"),
            ("execution_key", "\"" + ExecutionKey + "\""),
            ("sequence", "0"),
            ("producer_id", "\"producer-1\""),
            ("event_id", "\"" + EventId + "\""),
            ("schema_version", "1"),
            ("ext", """{"title":"t","schema":"playwright/1"}"""));

        Parse(Batch(forward)).Events[0].PayloadHash
            .ShouldBe(Parse(Batch(reversed)).Events[0].PayloadHash);
    }

    [Fact]
    public void Payload_Hash_Changes_When_A_Value_Changes()
    {
        var first = Parse(Batch(ValidEvent(sequence: 0))).Events[0].PayloadHash;
        var second = Parse(Batch(ValidEvent(sequence: 1))).Events[0].PayloadHash;

        first.ShouldNotBe(second);
    }

    // ---- shared canonicalisation vectors ----------------------------------------------------

    public static TheoryData<string> Rfc8785Vectors => new()
    {
        "arrays", "french", "structures", "unicode", "values", "weird",
    };

    [Theory]
    [MemberData(nameof(Rfc8785Vectors))]
    public void Rfc8785_Vector_Canonical_Bytes_Match_The_Shared_Output(string name)
    {
        var input = File.ReadAllBytes(Fixtures($"rfc8785/input/{name}.json"));
        var expected = File.ReadAllBytes(Fixtures($"rfc8785/output/{name}.json"));

        // The engine itself.
        JsonCanonicalizer.CanonicalizeUtf8(input).ShouldBe(expected);

        // The same document inside an event-shaped input: it travels as an allow-listed ext value and
        // its canonical bytes are still the shared vector's expected output.
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":""" + Encoding.UTF8.GetString(input) + "}")));

        result.IsAccepted.ShouldBeTrue();
        using var payload = JsonDocument.Parse(result.Events[0].PayloadJson);
        JsonCanonicalizer.CanonicalizeUtf8(payload.RootElement.GetProperty("location").GetRawText())
            .ShouldBe(expected);
    }

    public static TheoryData<string> CanonicalVectors => new()
    {
        "candidate", "evidence", "manifest", "policy",
    };

    [Theory]
    [MemberData(nameof(CanonicalVectors))]
    public void Canonical_Vector_Digest_And_Event_Shaped_Residue_Match(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Fixtures($"canonical/{name}.json")));
        var vector = document.RootElement;
        var input = vector.GetProperty("input");
        var canonical = vector.GetProperty("canonical").GetString()!;
        var digest = vector.GetProperty("sha256").GetString()!;

        CanonicalHash.Compute(input).ShouldBe(digest);
        JsonCanonicalizer.Canonicalize(input.GetRawText()).ShouldBe(canonical);

        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":""" + input.GetRawText() + "}")));

        result.IsAccepted.ShouldBeTrue();
        using var payload = JsonDocument.Parse(result.Events[0].PayloadJson);
        var kept = payload.RootElement.GetProperty("location");
        JsonCanonicalizer.Canonicalize(kept.GetRawText()).ShouldBe(canonical);
        CanonicalHash.Compute(kept).ShouldBe(digest);
    }
}
