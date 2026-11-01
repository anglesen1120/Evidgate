namespace Evidgate.Evaluation;

/// <summary>The waiver and quarantine snapshot resolved at trigger time: ids and their instant, never live rows.</summary>
public sealed record ExceptionsSnapshotInput
{
    private IReadOnlyList<Guid> waiverIds = [];
    private IReadOnlyList<Guid> quarantineIds = [];

    /// <summary>Creates the snapshot; both id lists are copied.</summary>
    /// <param name="asOf">The instant the resolver read the rows; the evaluator never reads a clock to get it.</param>
    /// <param name="waiverIds">The approved, unexpired waiver ids in scope.</param>
    /// <param name="quarantineIds">The active quarantine entry ids in scope.</param>
    public ExceptionsSnapshotInput(
        DateTimeOffset asOf,
        IReadOnlyList<Guid> waiverIds,
        IReadOnlyList<Guid> quarantineIds)
    {
        ArgumentNullException.ThrowIfNull(waiverIds);
        ArgumentNullException.ThrowIfNull(quarantineIds);

        AsOf = asOf;
        WaiverIds = waiverIds;
        QuarantineIds = quarantineIds;
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
}
