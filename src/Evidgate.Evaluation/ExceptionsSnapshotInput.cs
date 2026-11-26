namespace Evidgate.Evaluation;

/// <summary>The waiver and quarantine snapshot resolved at trigger time: ids and their instant, never live rows.</summary>
public sealed record ExceptionsSnapshotInput
{
    private IReadOnlyList<Guid> waiverIds = [];
    private IReadOnlyList<Guid> quarantineIds = [];
    private IReadOnlyList<Guid> quarantinedCaseIds = [];
    private IReadOnlyList<Guid> waivedCaseIds = [];

    /// <summary>Creates the snapshot; every id list is copied.</summary>
    /// <param name="asOf">The instant the resolver read the rows; the evaluator never reads a clock to get it.</param>
    /// <param name="waiverIds">The approved, unexpired waiver ids in scope.</param>
    /// <param name="quarantineIds">The active quarantine entry ids in scope.</param>
    /// <param name="quarantinedCaseIds">The catalog case ids with an active quarantine entry; none when omitted.</param>
    /// <param name="waivedCaseIds">The case ids with an approved, unexpired case-scope waiver; none when omitted.</param>
    public ExceptionsSnapshotInput(
        DateTimeOffset asOf,
        IReadOnlyList<Guid> waiverIds,
        IReadOnlyList<Guid> quarantineIds,
        IReadOnlyList<Guid>? quarantinedCaseIds = null,
        IReadOnlyList<Guid>? waivedCaseIds = null)
    {
        ArgumentNullException.ThrowIfNull(waiverIds);
        ArgumentNullException.ThrowIfNull(quarantineIds);

        AsOf = asOf;
        WaiverIds = waiverIds;
        QuarantineIds = quarantineIds;
        QuarantinedCaseIds = quarantinedCaseIds ?? [];
        WaivedCaseIds = waivedCaseIds ?? [];
    }

    /// <summary>The instant the resolver read the rows.</summary>
    public DateTimeOffset AsOf { get; init; }

    /// <summary>The approved, unexpired waiver ids in scope.</summary>
    public IReadOnlyList<Guid> WaiverIds
    {
        get => waiverIds;
        init => waiverIds = InputSnapshot.Copy(value);
    }

    /// <summary>The active quarantine entry ids in scope.</summary>
    public IReadOnlyList<Guid> QuarantineIds
    {
        get => quarantineIds;
        init => quarantineIds = InputSnapshot.Copy(value);
    }

    /// <summary>The catalog case ids with an active quarantine entry; they feed the evaluation, not the hash.</summary>
    public IReadOnlyList<Guid> QuarantinedCaseIds
    {
        get => quarantinedCaseIds;
        init => quarantinedCaseIds = InputSnapshot.Copy(value);
    }

    /// <summary>The case ids with an approved, unexpired case-scope waiver; they only annotate a failure.</summary>
    public IReadOnlyList<Guid> WaivedCaseIds
    {
        get => waivedCaseIds;
        init => waivedCaseIds = InputSnapshot.Copy(value);
    }
}
