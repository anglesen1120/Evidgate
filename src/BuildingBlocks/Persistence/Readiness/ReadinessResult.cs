namespace Evidgate.BuildingBlocks.Persistence.Readiness;

/// <summary>Machine-readable readiness outcomes (00 §9.1 <c>/internal/health/ready</c>).</summary>
public static class ReadinessReason
{
    /// <summary>Database reachable and the journal head equals the expected head.</summary>
    public const string Ready = "ready";

    /// <summary>The database could not be reached at all.</summary>
    public const string DatabaseUnreachable = "database_unreachable";

    /// <summary><c>public.schema_migration</c> does not exist — migrations were never applied.</summary>
    public const string JournalMissing = "journal_missing";

    /// <summary>The journal exists but its head is lexically older than this build's expected head.</summary>
    public const string JournalBehind = "journal_behind";

    /// <summary>
    /// The journal head is newer than this build expects. Ready: under the 00 §5.2
    /// expand/contract contract migrations land before app rollout and a rollback
    /// keeps the new schema with the old code, so old code must serve a newer
    /// journal (01.4.1 §11).
    /// </summary>
    public const string JournalAhead = "journal_ahead";

    /// <summary>The check itself failed for an unforeseen reason; never treated as ready.</summary>
    public const string CheckFailed = "check_failed";
}

/// <summary>
/// Result of one readiness probe: the journal head actually found, the head this
/// build expects, and why the process is (not) ready. Never carries the
/// connection string (00 §5.7 S3).
/// </summary>
public sealed record ReadinessResult(bool Ready, string Reason, string ExpectedHead, string? JournalHead)
{
    /// <summary>The process can serve work; the journal head equals the expected head.</summary>
    public static ReadinessResult IsReady(string expectedHead, string? journalHead) =>
        new(true, ReadinessReason.Ready, expectedHead, journalHead);

    /// <summary>The process can serve work; the journal head is newer than this build expects.</summary>
    public static ReadinessResult IsReadyAhead(string expectedHead, string journalHead) =>
        new(true, ReadinessReason.JournalAhead, expectedHead, journalHead);

    /// <summary>The process must not serve work; <paramref name="reason"/> says why.</summary>
    public static ReadinessResult NotReady(string reason, string expectedHead, string? journalHead) =>
        new(false, reason, expectedHead, journalHead);
}
