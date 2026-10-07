namespace Evidgate.Modules.Policy.Application;

/// <summary>
/// Why a stored <c>policy_revision</c> row no longer matches its content address (07.1.1-T2.2, finding T1-SEC-S2).
/// </summary>
internal static class PolicyRevisionCorruptionReasons
{
    /// <summary>The stored body no longer canonicalises (a duplicate name, a lone surrogate, a number outside the double range).</summary>
    internal const string NotCanonicalisable = "not_canonicalisable";

    /// <summary>The stored body carries a number whose exact decimal value is not a double (I-JSON, RFC 7493 §2.2).</summary>
    internal const string InexactNumber = "inexact_number";

    /// <summary>The stored body canonicalises, but to a different address than the stored <c>hash</c> column.</summary>
    internal const string HashMismatch = "hash_mismatch";
}

/// <summary>
/// A stored <c>policy_revision</c> row no longer matches its content address (07.1.1-T2.2, finding T1-SEC-S2): the
/// <c>body</c> read back from the database does not hash to the stored <c>hash</c> column, so the row was mutated
/// outside the append-only path (the app role holds no <c>UPDATE</c> grant) or the body no longer canonicalises.
/// The read fails closed instead of serving a revision under the wrong address. The body itself is never carried —
/// only the revision id, the two addresses and a fixed <see cref="Reason"/> — so a corrupted payload cannot leak
/// through a log or an error envelope, and the cause is never chained for the same reason.
/// </summary>
internal sealed class PolicyRevisionCorruptedException : Exception
{
    /// <summary>
    /// Creates the exception for a row that failed verification, with a fixed reason and no cause chained.
    /// </summary>
    /// <param name="revisionId">The revision whose stored row is corrupt.</param>
    /// <param name="storedHash">The content address stored on the row.</param>
    /// <param name="recomputedHash">The address recomputed from the stored body, or null when the body could not be verified.</param>
    /// <param name="reason">Why the row failed verification (<see cref="PolicyRevisionCorruptionReasons"/>); never body content.</param>
    public PolicyRevisionCorruptedException(Guid revisionId, string storedHash, string? recomputedHash, string reason)
        : base(Describe(revisionId, storedHash, recomputedHash, reason))
    {
        RevisionId = revisionId;
        StoredHash = storedHash;
        RecomputedHash = recomputedHash;
        Reason = reason;
    }

    /// <summary>The revision whose stored row is corrupt.</summary>
    public Guid RevisionId { get; }

    /// <summary>The content address stored on the row.</summary>
    public string StoredHash { get; }

    /// <summary>The address recomputed from the stored body, or null when the body could not be verified.</summary>
    public string? RecomputedHash { get; }

    /// <summary>Why the row failed verification (<see cref="PolicyRevisionCorruptionReasons"/>); never body content.</summary>
    public string Reason { get; }

    private static string Describe(Guid revisionId, string storedHash, string? recomputedHash, string reason) =>
        recomputedHash is null
            ? $"policy revision {revisionId} cannot be verified against its content address {storedHash}: {reason}"
            : $"policy revision {revisionId} does not match its content address: stored {storedHash}, recomputed {recomputedHash}";
}
