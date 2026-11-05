using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.GoldenHarness;

/// <summary>
/// Compares a case's expectation with what a parser produced. Events pair by
/// <c>(producer_id, sequence)</c>, not arrival order, and each pair is compared through its
/// RFC 8785 canonical form, so escaping or member order cannot produce a false difference.
/// </summary>
public static class GoldenDiff
{
    private const string DeclaredCountField = "declared_count";
    private const string RawReportStatusField = "raw_report_status";
    private const string EventField = "event";
    private const string ProducerIdField = "producer_id";
    private const string SequenceField = "sequence";
    private const string CaseKeyField = "case_key";
    private const string RawStatusField = "raw_status";
    private const string NormalisedStatusField = "normalised_status";

    /// <summary>Every difference, empty when the two documents agree.</summary>
    public static IReadOnlyList<DiffEntry> Compare(ExpectedDoc expected, ExpectedDoc observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        var diffs = new List<DiffEntry>();

        if (!string.Equals(expected.RawReportStatus, observed.RawReportStatus, StringComparison.Ordinal))
        {
            diffs.Add(new DiffEntry(
                null,
                RawReportStatusField,
                DiffReasons.StatusMismatch,
                expected.RawReportStatus,
                observed.RawReportStatus));
        }

        if (expected.DeclaredCount != observed.DeclaredCount)
        {
            diffs.Add(new DiffEntry(
                null,
                DeclaredCountField,
                DiffReasons.CountMismatch,
                Integer(expected.DeclaredCount),
                Integer(observed.DeclaredCount)));
        }

        CompareEvents(Sorted(expected.Events), Sorted(observed.Events), diffs);
        return diffs;
    }

    private static void CompareEvents(
        IReadOnlyList<NormalisedEvent> expected,
        IReadOnlyList<NormalisedEvent> observed,
        List<DiffEntry> diffs)
    {
        var (unpairedExpected, unpairedObserved) = ComparePairedEvents(expected, observed, diffs);
        CompareUnpairedEvents(expected, observed, unpairedExpected, unpairedObserved, diffs);
    }

    /// <summary>
    /// Compares the events both documents can identify and returns the ones no identity matched.
    /// </summary>
    private static (List<int> UnpairedExpected, List<int> UnpairedObserved) ComparePairedEvents(
        IReadOnlyList<NormalisedEvent> expected,
        IReadOnlyList<NormalisedEvent> observed,
        List<DiffEntry> diffs)
    {
        var observedByKey = new Dictionary<EventIdentity, int>(observed.Count);
        for (var index = 0; index < observed.Count; index++)
        {
            observedByKey.TryAdd(Identity(observed[index]), index);
        }

        var pairedObserved = new bool[observed.Count];
        var unpairedExpected = new List<int>();
        for (var index = 0; index < expected.Count; index++)
        {
            if (observedByKey.TryGetValue(Identity(expected[index]), out var observedIndex)
                && !pairedObserved[observedIndex])
            {
                pairedObserved[observedIndex] = true;
                CompareEvent(index, expected[index], observed[observedIndex], diffs);
                continue;
            }

            unpairedExpected.Add(index);
        }

        var unpairedObserved = new List<int>();
        for (var index = 0; index < observed.Count; index++)
        {
            if (!pairedObserved[index])
            {
                unpairedObserved.Add(index);
            }
        }

        return (unpairedExpected, unpairedObserved);
    }

    private static void CompareUnpairedEvents(
        IReadOnlyList<NormalisedEvent> expected,
        IReadOnlyList<NormalisedEvent> observed,
        List<int> unpairedExpected,
        List<int> unpairedObserved,
        List<DiffEntry> diffs)
    {
        // What no identity could pair is compared positionally, so a renumbered sequence reads
        // as one field difference instead of a missing event plus an extra one.
        var paired = Math.Min(unpairedExpected.Count, unpairedObserved.Count);
        for (var index = 0; index < paired; index++)
        {
            var expectedIndex = unpairedExpected[index];
            CompareEvent(expectedIndex, expected[expectedIndex], observed[unpairedObserved[index]], diffs);
        }

        for (var index = paired; index < unpairedExpected.Count; index++)
        {
            var expectedIndex = unpairedExpected[index];
            diffs.Add(new DiffEntry(
                expectedIndex,
                EventField,
                DiffReasons.MissingEvent,
                Describe(expected[expectedIndex]),
                null));
        }

        for (var index = paired; index < unpairedObserved.Count; index++)
        {
            var observedIndex = unpairedObserved[index];
            diffs.Add(new DiffEntry(
                observedIndex,
                EventField,
                DiffReasons.ExtraEvent,
                null,
                Describe(observed[observedIndex])));
        }
    }

    private static void CompareEvent(
        int eventIndex,
        NormalisedEvent expected,
        NormalisedEvent observed,
        List<DiffEntry> diffs)
    {
        var expectedCanonical = CanonicalEvent(expected);
        var observedCanonical = CanonicalEvent(observed);
        if (expectedCanonical.AsSpan().SequenceEqual(observedCanonical))
        {
            return;
        }

        using var expectedDocument = JsonDocument.Parse(expectedCanonical);
        using var observedDocument = JsonDocument.Parse(observedCanonical);

        foreach (var property in expectedDocument.RootElement.EnumerateObject())
        {
            var observedValue = observedDocument.RootElement.GetProperty(property.Name);
            if (property.Value.GetRawText().AsSpan().SequenceEqual(observedValue.GetRawText()))
            {
                continue;
            }

            diffs.Add(new DiffEntry(
                eventIndex,
                property.Name,
                ReasonFor(property.Name),
                Display(property.Value),
                Display(observedValue)));
        }
    }

    private static byte[] CanonicalEvent(NormalisedEvent value)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(ProducerIdField, value.ProducerId);
            writer.WriteNumber(SequenceField, value.Sequence);
            writer.WriteString(CaseKeyField, value.CaseKey);
            writer.WriteString(RawStatusField, value.RawStatus);
            writer.WriteString(NormalisedStatusField, value.NormalisedStatus);
            writer.WriteEndObject();
        }

        return JsonCanonicalizer.CanonicalizeUtf8(buffer.WrittenSpan);
    }

    private static IReadOnlyList<NormalisedEvent> Sorted(IReadOnlyList<NormalisedEvent> events) =>
    [
        .. events
            .OrderBy(value => value.ProducerId, StringComparer.Ordinal)
            .ThenBy(value => value.Sequence),
    ];

    private static EventIdentity Identity(NormalisedEvent value) => new(value.ProducerId, value.Sequence);

    private static string ReasonFor(string field) => field switch
    {
        CaseKeyField => DiffReasons.UnmappedCase,
        RawStatusField or NormalisedStatusField => DiffReasons.StatusMismatch,
        _ => DiffReasons.FieldMismatch,
    };

    private static string Display(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.ToString() : value.GetRawText();

    private static string Describe(NormalisedEvent value) =>
        $"{value.ProducerId}#{Integer(value.Sequence)} {value.CaseKey} {value.RawStatus}->{value.NormalisedStatus}";

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The identity events are paired by: one sequence per producer names one event.</summary>
    private readonly record struct EventIdentity(string ProducerId, long Sequence);
}
