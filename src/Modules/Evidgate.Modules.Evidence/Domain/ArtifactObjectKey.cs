namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The code statement of the 0028 <c>artifact_object_key_shape_check</c>, shared by every reader of a stored artifact:
/// the key a row holds must be the key that row's own coordinates mint, so a parse can never read bytes another
/// coordinate names. One copy, so the parse task and the report parse cannot drift on the guard.
/// </summary>
internal static class ArtifactObjectKey
{
    /// <summary>The message of the thrown guard: named for an operator, never carrying a key or a byte (S3).</summary>
    public const string MismatchMessage =
        "the artifact's stored object key is not the key its row mints, so the report was not read; an operator must look at this artifact";

    /// <summary>
    /// True when the row's stored <c>object_key</c> is exactly the final key the row's own coordinates mint
    /// (<see cref="ArtifactKeyMinter.Mint"/>). The 0028 shape CHECK enforces this in the database; this is the same
    /// statement in code, so the read never names an object the row does not own.
    /// </summary>
    /// <param name="artifact">The row about to be read.</param>
    /// <returns><see langword="true"/> when the key is the row's minted final key.</returns>
    public static bool IsMintedFor(Artifact artifact)
    {
        string minted;

        try
        {
            minted = ArtifactKeyMinter.Mint(
                artifact.OrgId,
                artifact.ProjectId,
                artifact.RunId,
                artifact.ExecutionKey,
                artifact.AttemptNo,
                artifact.Id);
        }
        catch (ArgumentException)
        {
            // Coordinates the CHECK constraints should have refused (a malformed execution key, attempt_no below 1):
            // no key is minted for them, so the row is a mismatch rather than a read.
            return false;
        }

        return string.Equals(artifact.ObjectKey, minted, StringComparison.Ordinal);
    }
}
