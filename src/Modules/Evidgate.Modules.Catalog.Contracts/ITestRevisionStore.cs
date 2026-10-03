namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// Append-only store of test revisions (03.1.3), exported to the sync of 03.4.1 and to the AI drafts of 03.7.1
/// (00 §5.5). Only hashes and a small metadata object are stored — never the source (S3).
/// </summary>
public interface ITestRevisionStore
{
    /// <summary>
    /// Records the version of a test that <paramref name="content"/> describes, or finds the row that already holds
    /// it: the same source, framework and language always give the same revision, and a change of the file path alone
    /// gives the same revision too. Must run inside a tenant transaction.
    /// </summary>
    /// <param name="caseId">The case; another tenant's id fails the composite foreign key.</param>
    /// <param name="content">The version to record.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<TestRevisionAppend> AppendAsync(Guid caseId, TestRevisionContent content, CancellationToken cancellationToken);
}

/// <summary>One version of a test as the sync sees it.</summary>
/// <param name="CommitSha">Lower-case hex commit that holds this version.</param>
/// <param name="FilePath">Path at that commit; kept on the row of the first sync of this content.</param>
/// <param name="Framework">Framework name (decides how assertions are extracted).</param>
/// <param name="Language">Language, when known.</param>
/// <param name="Source">The exact source text; it is hashed and discarded.</param>
public sealed record TestRevisionContent(string CommitSha, string FilePath, string Framework, string? Language, string Source);

/// <summary>Result of <see cref="ITestRevisionStore.AppendAsync"/>.</summary>
/// <param name="RevisionId">The revision row (new or existing).</param>
/// <param name="RevisionHash"><c>sha256:</c> identity of the version.</param>
/// <param name="Created">True when this call inserted the row.</param>
public sealed record TestRevisionAppend(Guid RevisionId, string RevisionHash, bool Created);
