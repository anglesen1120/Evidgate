using System.Globalization;
using System.Text;
using System.Text.Json;
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
public sealed partial class EventBatchParserUnitTests
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
        { EventWith("epoch", "1.5"), "/events/0/epoch", "out_of_range" },
        { EventWith("epoch", "\"2\""), "/events/0/epoch", "invalid_type" },
        { EventWith("epoch", "null"), "/events/0/epoch", "invalid_type" },
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

    /// <summary>An event carrying the base members plus one extra member (an unknown envelope member).</summary>
    private static string EventPlus(string name, string value)
    {
        var members = BaseMembers().ToList();
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
