namespace Evidgate.Modules.Policy.Application;

/// <summary>
/// A stored <c>policy_revision</c> row no longer matches its content address (07.1.1-T2.2, finding T1-SEC-S2): the
/// <c>body</c> read back from the database does not hash to the stored <c>hash</c> column, so the row was mutated
/// outside the append-only path (the app role holds no <c>UPDATE</c> grant) or the body no longer canonicalises.
/// The read fails closed instead of serving a revision under the wrong address. The body itself is never carried —
/// only the revision id and the two addresses — so a corrupted payload cannot leak through a log or an error
/// envelope.
/// </summary>
internal sealed class PolicyRevisionCorruptedException : Exception
{
    /// <summary>Creates the exception for a row whose recomputed address differs from the stored one.</summary>
    public PolicyRevisionCorruptedException(Guid revisionId, string storedHash, string recomputedHash)
        : base($"policy revision {revisionId} does not match its content address: stored {storedHash}, recomputed {recomputedHash}")
    {
        RevisionId = revisionId;
        StoredHash = storedHash;
        RecomputedHash = recomputedHash;
    }

    /// <summary>Creates the exception for a body that no longer canonicalises, with the underlying cause.</summary>
    public PolicyRevisionCorruptedException(Guid revisionId, string storedHash, Exception innerException)
        : base($"policy revision {revisionId} cannot be canonicalised to verify its content address {storedHash}", innerException)
    {
        RevisionId = revisionId;
        StoredHash = storedHash;
    }

    /// <summary>The revision whose stored row is corrupt.</summary>
    public Guid RevisionId { get; }

    /// <summary>The content address stored on the row.</summary>
    public string StoredHash { get; }

    /// <summary>The address recomputed from the stored body, or null when the body could not be canonicalised.</summary>
    public string? RecomputedHash { get; }
}
