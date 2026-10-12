using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Ingestion.Domain.Events;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.1-T1 without a database: the pure batch-envelope parser — the happy path, every rejection
/// reason with its 00 §9.2 <c>index</c>/<c>path</c>, the batch caps, hostile JSON, the RFC 8785
/// <c>payload_hash</c> (including the shared <c>fixtures/rfc8785</c> and <c>fixtures/canonical</c>
/// vectors the CLI asserts too) and the <c>ext</c> allow-list residue.
/// </summary>
public sealed class EventBatchParserUnitTests
{
    private const string EventId = "0191f0a0-0000-7000-8000-000000000001";
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SchemaExt = """{"schema":"playwright/1"}""";

    // ---- happy path -------------------------------------------------------------------------

    [Fact]
    public void Valid_Batch_Parses_Every_Event_In_Request_Order()
    {
        var result = Parse(Batch(
            ValidEvent(type: "test.started", sequence: 0),
            ValidEvent(type: "test.finished", sequence: 1, status: "failed")));

        result.IsAccepted.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        result.Events.Count.ShouldBe(2);

        var started = result.Events[0];
        started.Index.ShouldBe(0);
        started.EventId.ShouldBe(Guid.Parse(EventId));
        started.ProducerId.ShouldBe("producer-1");
        started.Sequence.ShouldBe(0);
        started.AttemptNo.ShouldBe(1);
        started.Type.ShouldBe("test.started");
        started.RawStatus.ShouldBeNull();
        started.JobId.ShouldBeNull();
        started.Epoch.ShouldBeNull();
        started.DurationMs.ShouldBeNull();
        started.ArtifactIds.ShouldBeEmpty();
        started.OccurredAt.ShouldBe(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
        started.Xfail.ShouldBeFalse();
        started.PayloadJson.ShouldBe(SchemaExt);
        started.PayloadHash.ShouldStartWith("sha256:");
        started.PayloadHash.Length.ShouldBe("sha256:".Length + 64);

        var finished = result.Events[1];
        finished.Index.ShouldBe(1);
        finished.Sequence.ShouldBe(1);
        finished.RawStatus.ShouldBe("failed");
    }

    [Fact]
    public void Optional_Members_Are_Carried_Through()
    {
        var eventJson = Event(
            ("schema_version", "1"),
            ("event_id", "\"" + EventId + "\""),
            ("producer_id", "\"producer-1\""),
            ("sequence", "7"),
            ("job_id", "\"0191f0a0-0000-7000-8000-0000000000aa\""),
            ("epoch", "3"),
            ("execution_key", "\"" + ExecutionKey + "\""),
            ("attempt_no", "2"),
            ("type", "\"test.finished\""),
            ("status", "\"timedOut\""),
            ("duration_ms", "1280"),
            ("occurred_at", "\"2026-10-13T10:00:00.250+02:00\""),
            ("artifact_ids", "[\"" + EventId + "\"]"),
            ("ext", SchemaExt));

        var result = Parse(Batch(eventJson));

        result.IsAccepted.ShouldBeTrue();
        var parsed = result.Events[0];
        parsed.JobId.ShouldBe(Guid.Parse("0191f0a0-0000-7000-8000-0000000000aa"));
        parsed.Epoch.ShouldBe(3);
        parsed.AttemptNo.ShouldBe(2);
        parsed.DurationMs.ShouldBe(1280);
        parsed.RawStatus.ShouldBe("timedOut");
        parsed.ArtifactIds.Count.ShouldBe(1);
        parsed.ArtifactIds[0].ShouldBe(Guid.Parse(EventId));
        parsed.OccurredAt.ShouldBe(
            new DateTimeOffset(2026, 10, 13, 10, 0, 0, 250, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Parsed_Element_Outlives_The_Parse()
    {
        // The parse document is disposed before the element is read: a clone, not a view.
        var result = Parse(Batch(ValidEvent()));

        var element = result.Events[0].Element;
        element.ValueKind.ShouldBe(JsonValueKind.Object);
        element.GetProperty("type").GetString().ShouldBe("test.started");
        element.GetProperty("ext").GetProperty("schema").GetString().ShouldBe("playwright/1");
    }

    [Fact]
    public void Five_Hundred_Events_Are_Accepted()
    {
        var events = Enumerable.Range(0, 500).Select(index => ValidEvent(sequence: index)).ToArray();

        var result = Parse(Batch(events));

        result.IsAccepted.ShouldBeTrue();
        result.Events.Count.ShouldBe(500);
        result.Events[499].Sequence.ShouldBe(499);
    }

    // ---- per-event rejections ---------------------------------------------------------------

    public static TheoryData<string, string, string> InvalidEvents => new()
    {
        { EventWithout("schema_version"), "/events/0/schema_version", "required" },
        { EventWith("schema_version", "2"), "/events/0/schema_version", "invalid_value" },
        { EventWith("schema_version", "0"), "/events/0/schema_version", "out_of_range" },
        { EventWith("event_id", "\"not-a-uuid\""), "/events/0/event_id", "invalid_format" },
        { EventWith("event_id", "\"00000000-0000-0000-0000-000000000000\""), "/events/0/event_id", "invalid_format" },
        { EventWith("producer_id", "7"), "/events/0/producer_id", "invalid_type" },
        { EventWith("producer_id", "\"" + new string('p', 129) + "\""), "/events/0/producer_id", "too_long" },
        { EventWith("sequence", "-1"), "/events/0/sequence", "out_of_range" },
        { EventWith("sequence", "\"5\""), "/events/0/sequence", "invalid_type" },
        { EventWith("sequence", "1.5"), "/events/0/sequence", "out_of_range" },
        { EventWith("sequence", "9223372036854775808"), "/events/0/sequence", "out_of_range" },
        { EventWith("execution_key", "\"" + new string('0', 63) + "\""), "/events/0/execution_key", "invalid_format" },
        { EventWith("execution_key", "\"" + ExecutionKey.ToUpperInvariant() + "\""), "/events/0/execution_key", "invalid_format" },
        { EventWith("attempt_no", "0"), "/events/0/attempt_no", "out_of_range" },
        { EventWith("job_id", "7"), "/events/0/job_id", "invalid_format" },
        { EventWith("job_id", "\"nope\""), "/events/0/job_id", "invalid_format" },
        { EventWith("epoch", "-1"), "/events/0/epoch", "out_of_range" },
        { EventWith("duration_ms", "-1"), "/events/0/duration_ms", "out_of_range" },
        { EventWith("type", "\"test.unknown\""), "/events/0/type", "event_type_unknown" },
        { EventWith("type", "\"Test.Started\""), "/events/0/type", "event_type_unknown" },
        { EventWith("type", "\"test.finished\""), "/events/0/status", "required" },
        { EventWith("status", "7"), "/events/0/status", "invalid_type" },
        { EventWith("status", "\"" + new string('s', 65) + "\""), "/events/0/status", "too_long" },
        { EventWith("occurred_at", "\"2026-10-13\""), "/events/0/occurred_at", "invalid_format" },
        { EventWith("occurred_at", "\"2026-10-13T10:00:00\""), "/events/0/occurred_at", "invalid_format" },
        { EventWith("artifact_ids", "\"nope\""), "/events/0/artifact_ids", "invalid_type" },
        { EventWith("artifact_ids", "[\"" + EventId + "\",\"nope\"]"), "/events/0/artifact_ids/1", "invalid_format" },
        { EventWith("ext", "\"x\""), "/events/0/ext", "invalid_type" },
        { EventWith("ext", "{}"), "/events/0/ext/schema", "required" },
        { EventWith("ext", """{"schema":7}"""), "/events/0/ext/schema", "invalid_type" },
    };

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void Invalid_Event_Rejects_The_Batch_With_Path_Reason_And_Index(
        string eventJson,
        string path,
        string reason)
    {
        var result = Parse(Batch(eventJson));

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe(path);
        result.Errors[0].Reason.ShouldBe(reason);
        result.Errors[0].Index.ShouldBe(0);
    }

    [Fact]
    public void Invalid_Event_Of_A_Later_Position_Carries_Its_Own_Index()
    {
        var result = Parse(Batch(
            ValidEvent(sequence: 0),
            EventWith("sequence", "-1")));

        result.IsAccepted.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/1/sequence");
        result.Errors[0].Index.ShouldBe(1);
    }

    [Fact]
    public void Producer_Mismatch_Rejects_At_The_First_Deviating_Index()
    {
        var result = Parse(Batch(
            ValidEvent(producerId: "producer-a", sequence: 0),
            ValidEvent(producerId: "producer-a", sequence: 1),
            ValidEvent(producerId: "producer-b", sequence: 2),
            ValidEvent(producerId: "producer-b", sequence: 3)));

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/2/producer_id");
        result.Errors[0].Reason.ShouldBe("producer_mismatch");
        result.Errors[0].Index.ShouldBe(2);
    }

    // ---- batch-level rejections -------------------------------------------------------------

    public static TheoryData<string, string, string> InvalidBatches => new()
    {
        { """{"events":[]}""", "/events", "empty" },
        { """{"schema_version":1}""", "/events", "required" },
        { """{"events":{}}""", "/events", "invalid_type" },
        { "[1,2]", "", "invalid_type" },
        { "{", "", "malformed" },
        { Batch(ValidEvent()) + "{}", "", "malformed" },
        { ValidEvent(), "/events", "required" },
        { Batch(Enumerable.Repeat(ValidEvent(), 501).ToArray()), "/events", "too_large" },
        { """{"schema_version":2,"events":[""" + ValidEvent() + "]}", "/schema_version", "invalid_value" },
    };

    [Theory]
    [MemberData(nameof(InvalidBatches))]
    public void Invalid_Batch_Is_Rejected_With_Path_And_Reason(string json, string path, string reason)
    {
        var result = Parse(json);

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe(path);
        result.Errors[0].Reason.ShouldBe(reason);
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

    // ---- ext allow-list ---------------------------------------------------------------------

    [Fact]
    public void Unknown_Ext_Keys_Are_Dropped()
    {
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","evil":"x","also":1}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(SchemaExt);
    }

    [Fact]
    public void Adapter_Normalized_Status_Is_Dropped_While_The_Other_Status_Keys_Are_Kept()
    {
        var result = Parse(Batch(ValidEvent(
            type: "test.finished",
            status: "failed",
            ext: """{"schema":"playwright/1","normalized":"failed","skip_reason":"fixme","error_summary":"boom"}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(
            """{"schema":"playwright/1","skip_reason":"fixme","error_summary":"boom"}""");
    }

    [Fact]
    public void Capability_Carrier_Is_Not_Stored()
    {
        var result = Parse(Batch(ValidEvent(
            type: "run.log",
            ext: """{"schema":"playwright/1","capability":{"protocol":{"major":1}},"marker":"run_begin"}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe("""{"schema":"playwright/1","marker":"run_begin"}""");
        result.Events[0].Element.GetProperty("ext").GetProperty("capability").ValueKind
            .ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void Run_Log_Keys_Are_Not_Kept_On_A_Test_Envelope()
    {
        var result = Parse(Batch(ValidEvent(
            type: "test.started",
            ext: """{"schema":"playwright/1","marker":"run_begin","warn":{"code":"x"}}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(SchemaExt);
    }

    [Theory]
    [InlineData("test.started", false, SchemaExt)]
    [InlineData("test.finished", true, """{"schema":"playwright/1","xfail":true}""")]
    public void Xfail_Is_Read_Only_From_Test_Finished(string type, bool expected, string payload)
    {
        var result = Parse(Batch(ValidEvent(
            type: type,
            status: "failed",
            ext: """{"schema":"playwright/1","xfail":true}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].Xfail.ShouldBe(expected);
        result.Events[0].PayloadJson.ShouldBe(payload);
    }

    [Fact]
    public void Non_Boolean_Xfail_Does_Not_Set_The_Flag()
    {
        var result = Parse(Batch(ValidEvent(
            type: "test.finished",
            status: "passed",
            ext: """{"schema":"playwright/1","xfail":"yes"}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].Xfail.ShouldBeFalse();
    }

    [Fact]
    public void Secret_Shaped_Key_Names_Are_Dropped_At_Any_Depth()
    {
        var result = Parse(Batch(ValidEvent(ext: """
            {"schema":"playwright/1","location":{"file":"a.ts","line":3,"token":"planted","api_key":"planted"},"authorization":"planted","password":"planted","project":"p"}
            """)));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(
            """{"schema":"playwright/1","location":{"file":"a.ts","line":3},"project":"p"}""");
        result.Events[0].PayloadJson.ShouldNotContain("planted");
    }

    [Fact]
    public void An_Envelope_Without_Ext_Leaves_An_Empty_Residue()
    {
        var result = Parse(Batch(ValidEvent(ext: null)));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe("{}");
    }

    [Fact]
    public void An_Ext_Residue_Under_The_Cap_Is_Kept()
    {
        var filler = new string('x', 8000);
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":{"file":""" + "\"" + filler + "\"}}")));

        result.IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public void An_Ext_Residue_Over_The_Cap_Is_Refused()
    {
        var filler = new string('x', 9000);
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":{"file":""" + "\"" + filler + "\"}}")));

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/0/ext");
        result.Errors[0].Reason.ShouldBe("ext_too_large");
        result.Errors[0].Index.ShouldBe(0);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static EventBatchParseResult Parse(string json) =>
        EventBatchParser.Parse(Encoding.UTF8.GetBytes(json));

    private static string Batch(params string[] events) =>
        "{\"schema_version\":1,\"events\":[" + string.Join(",", events) + "]}";

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ValidEvent(
        string type = "test.started",
        string eventId = EventId,
        string producerId = "producer-1",
        long sequence = 0,
        long attemptNo = 1,
        string? status = null,
        string? ext = SchemaExt)
    {
        var json = new StringBuilder()
            .Append("{\"schema_version\":1,\"event_id\":\"").Append(eventId)
            .Append("\",\"producer_id\":\"").Append(producerId)
            .Append("\",\"sequence\":").Append(Num(sequence))
            .Append(",\"execution_key\":\"").Append(ExecutionKey)
            .Append("\",\"attempt_no\":").Append(Num(attemptNo))
            .Append(",\"type\":\"").Append(type)
            .Append("\",\"occurred_at\":\"2026-10-13T10:00:00Z\"");

        if (status is not null)
        {
            json.Append(",\"status\":\"").Append(status).Append('"');
        }

        if (ext is not null)
        {
            json.Append(",\"ext\":").Append(ext);
        }

        return json.Append('}').ToString();
    }

    private static string Event(params (string Name, string Value)[] members) =>
        "{" + string.Join(",", members.Select(member => "\"" + member.Name + "\":" + member.Value)) + "}";

    private static (string Name, string Value)[] BaseMembers() =>
    [
        ("schema_version", "1"),
        ("event_id", "\"" + EventId + "\""),
        ("producer_id", "\"producer-1\""),
        ("sequence", "0"),
        ("execution_key", "\"" + ExecutionKey + "\""),
        ("attempt_no", "1"),
        ("type", "\"test.started\""),
        ("occurred_at", "\"2026-10-13T10:00:00Z\""),
        ("ext", SchemaExt),
    ];

    private static string EventWith(string name, string value)
    {
        var members = BaseMembers()
            .Where(member => !string.Equals(member.Name, name, StringComparison.Ordinal))
            .ToList();
        members.Add((name, value));
        return Event(members.ToArray());
    }

    private static string EventWithout(string name) =>
        Event(BaseMembers()
            .Where(member => !string.Equals(member.Name, name, StringComparison.Ordinal))
            .ToArray());

    /// <summary>A file under the repository's <c>fixtures/</c> directory, resolved as the suite's fixture does.</summary>
    private static string Fixtures(string relativePath) =>
        Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", relativePath);
}
