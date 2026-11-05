using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evidgate.GoldenHarness;

/// <summary>
/// Strict readers for <c>case.json</c> and <c>expected.json</c>: a BOM, invalid UTF-8, a
/// duplicate member name or a missing field is refused with the fixture named, because an
/// entry that reads two ways pins two corpora. The test-only loader holds the same refusals.
/// </summary>
internal static class FixtureReaders
{
    internal const string CaseFileName = "case.json";
    internal const string ExpectedFileName = "expected.json";

    private const string PendingStatus = "pending";
    private const string MalformedStatus = "malformed";

    private static readonly string[] RawReportStatuses = ["parsed", MalformedStatus, "unsupported"];

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads <c>case.json</c> of one case directory.</summary>
    internal static CaseMeta ReadMeta(string fixture, string directory)
    {
        var json = ReadObject(fixture, directory, CaseFileName);

        return new CaseMeta(
            RequiredString(fixture, CaseFileName, json, "producer_id"),
            RequiredString(fixture, CaseFileName, json, "parser_id"),
            RequiredString(fixture, CaseFileName, json, "parser_version"),
            RequiredString(fixture, CaseFileName, json, "rationale"),
            OptionalParserStatus(fixture, json));
    }

    /// <summary>Reads <c>expected.json</c> of one case directory.</summary>
    internal static ExpectedDoc ReadExpected(string fixture, string directory)
    {
        var json = ReadObject(fixture, directory, ExpectedFileName);

        var rawReportStatus = RequiredString(fixture, ExpectedFileName, json, "raw_report_status");
        if (!RawReportStatuses.Contains(rawReportStatus, StringComparer.Ordinal))
        {
            throw new FixtureLayoutException(
                fixture,
                $"'{ExpectedFileName}' field 'raw_report_status' must be one of {string.Join(", ", RawReportStatuses)}");
        }

        var declaredCount = RequiredInteger(fixture, ExpectedFileName, json, "declared_count");
        if (declaredCount > int.MaxValue)
        {
            throw new FixtureLayoutException(
                fixture,
                $"'{ExpectedFileName}' field 'declared_count' is larger than an event count can be");
        }

        if (json["events"] is not JsonArray events)
        {
            throw new FixtureLayoutException(
                fixture,
                $"'{ExpectedFileName}' field 'events' must be an array");
        }

        // A malformed report yielded nothing to map, so an expectation that is malformed and
        // still counts or lists events declares two outcomes at once and can never be compared
        // in a way a reader can trust.
        if (string.Equals(rawReportStatus, MalformedStatus, StringComparison.Ordinal)
            && (declaredCount != 0 || events.Count > 0))
        {
            throw new FixtureLayoutException(
                fixture,
                $"'{ExpectedFileName}' declares 'raw_report_status' '{MalformedStatus}', so 'declared_count' must be 0 and 'events' must be empty");
        }

        var normalised = new List<NormalisedEvent>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            normalised.Add(ReadEvent(fixture, events[index], index));
        }

        return new ExpectedDoc(rawReportStatus, (int)declaredCount, normalised);
    }

    private static NormalisedEvent ReadEvent(string fixture, JsonNode? node, int index)
    {
        if (node is not JsonObject json)
        {
            throw new FixtureLayoutException(
                fixture,
                $"'{ExpectedFileName}' events[{index}] must be a JSON object");
        }

        // The event member names are the diff's field names, so both readers of an event
        // (this one and the diff) spell them once, here.
        return new NormalisedEvent(
            RequiredString(fixture, ExpectedFileName, json, "producer_id"),
            RequiredInteger(fixture, ExpectedFileName, json, "sequence"),
            RequiredString(fixture, ExpectedFileName, json, "case_key"),
            RequiredString(fixture, ExpectedFileName, json, "raw_status"),
            RequiredString(fixture, ExpectedFileName, json, "normalised_status"));
    }

    private static JsonObject ReadObject(string fixture, string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FixtureLayoutException(fixture, fileName + " is missing");
        }

        var bytes = File.ReadAllBytes(path);
        if (HasUtf8Bom(bytes))
        {
            throw new FixtureLayoutException(fixture, $"'{fileName}' must be UTF-8 without BOM");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new FixtureLayoutException(fixture, $"'{fileName}' is not valid UTF-8: {error.Message}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
            if (node is not null)
            {
                // JsonNode.Parse builds objects lazily, so a duplicate member name only throws
                // when the dictionary materializes; doing that inside the try keeps it here.
                Materialize(node);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new FixtureLayoutException(fixture, $"'{fileName}' is not valid JSON: {error.Message}");
        }

        return node as JsonObject
            ?? throw new FixtureLayoutException(fixture, $"'{fileName}' must be a JSON object");
    }

    private static string RequiredString(string fixture, string fileName, JsonObject json, string field)
    {
        var node = json[field];
        if (node?.GetValueKind() == JsonValueKind.String
            && node.GetValue<string>() is { } text
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new FixtureLayoutException(
            fixture,
            $"'{fileName}' field '{field}' must be a non-empty string");
    }

    private static long RequiredInteger(string fixture, string fileName, JsonObject json, string field)
    {
        if (json[field] is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<long>(out var number)
            && number >= 0)
        {
            return number;
        }

        throw new FixtureLayoutException(
            fixture,
            $"'{fileName}' field '{field}' must be a non-negative integer");
    }

    private static string? OptionalParserStatus(string fixture, JsonObject json)
    {
        var node = json["parser_status"];
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.String
            && string.Equals(node.GetValue<string>(), PendingStatus, StringComparison.Ordinal))
        {
            return PendingStatus;
        }

        // Only a declared status is accepted: a typo would otherwise silently change the
        // verdict of the case, which is the one thing a golden fixture must never do.
        throw new FixtureLayoutException(
            fixture,
            $"'{CaseFileName}' field 'parser_status', when present, must be \"{PendingStatus}\"");
    }

    private static void Materialize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                // Count forces the lazy member dictionary to materialize, and a duplicate
                // member name throws there rather than at parse time.
                _ = jsonObject.Count;
                foreach (var member in jsonObject.ToArray())
                {
                    if (member.Value is not null)
                    {
                        Materialize(member.Value);
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (var element in jsonArray.ToArray())
                {
                    if (element is not null)
                    {
                        Materialize(element);
                    }
                }

                break;
        }
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
