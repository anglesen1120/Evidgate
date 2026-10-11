using System.Globalization;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The one place an artifact object key is built (06.1.1 §5, FR-06-1): a pure function, no I/O, so the key is
/// server-chosen and a client can never name, collide with or traverse one. The produced string is exactly the
/// final form the 0028 <c>artifact_object_key_shape_check</c> accepts
/// (<c>org/&lt;org_id&gt;/project/&lt;project_id&gt;/run/&lt;run_id&gt;/attempt/&lt;execution_key&gt;/&lt;attempt_no&gt;/&lt;artifact_id&gt;</c>),
/// with every id rendered in the lowercase <c>D</c> form Postgres' <c>uuid::text</c> produces and the attempt number
/// in the invariant culture, so the CHECK and this function can never drift.
/// <para>
/// The minter refuses an <paramref name="executionKey"/> that is not 64 lowercase hex characters and an
/// <paramref name="attemptNo"/> below 1 with an <see cref="ArgumentException"/>: it is a pure domain function and
/// callers validate first (the C2 shape validator), so a client-controlled string can never contribute a path
/// segment — no <c>/</c>, no <c>..</c>, no uppercase hex.
/// </para>
/// </summary>
internal static partial class ArtifactKeyMinter
{
    /// <summary>The 00 §7 execution-key shape: sha256 hex, lowercase (the 0028 <c>artifact_execution_key_check</c>).</summary>
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionKeyShape();

    /// <summary>
    /// The final object key of one artifact (06.1.1 §2 AC2, §5). <paramref name="executionKey"/> must be 64 lowercase
    /// hex characters and <paramref name="attemptNo"/> at least 1; every id is rendered lowercase <c>D</c>.
    /// </summary>
    /// <param name="org">The owning organization (<c>evidgate.org_id</c>).</param>
    /// <param name="project">The project the artifact belongs to.</param>
    /// <param name="run">The run the artifact was captured in.</param>
    /// <param name="executionKey">The attempt's execution key, sha256 hex (00 §7).</param>
    /// <param name="attemptNo">The attempt number within the execution key, at least 1.</param>
    /// <param name="artifactId">The artifact's own UUIDv7 id.</param>
    /// <returns>The server-chosen object key.</returns>
    /// <exception cref="ArgumentException">The execution key is not 64 lowercase hex, or the attempt number is below 1.</exception>
    public static string Mint(Guid org, Guid project, Guid run, string executionKey, int attemptNo, Guid artifactId)
    {
        if (executionKey is null || !ExecutionKeyShape().IsMatch(executionKey))
        {
            throw new ArgumentException(
                "the execution key must be 64 lowercase hex characters (00 §7); callers validate before minting",
                nameof(executionKey));
        }

        if (attemptNo < 1)
        {
            throw new ArgumentException(
                "the attempt number must be at least 1 (0028 artifact_attempt_no_check); callers validate before minting",
                nameof(attemptNo));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"org/{org:D}/project/{project:D}/run/{run:D}/attempt/{executionKey}/{attemptNo}/{artifactId:D}");
    }

    /// <summary>
    /// The deterministic staging key of one artifact (06.1.2 §5): <c>pending/&lt;org_id&gt;/&lt;artifact_id&gt;</c>,
    /// the other form the 0028 <c>artifact_object_key_shape_check</c> accepts. The row keeps the final key
    /// (<see cref="Mint"/>); this is the key the client <c>PUT</c>s to and the key the presign signs, so a
    /// client can never name it and the promotion to the final key is a server-side copy (FR-06-1, T86).
    /// </summary>
    /// <param name="org">The owning organization (<c>evidgate.org_id</c>).</param>
    /// <param name="artifactId">The artifact's own UUIDv7 id.</param>
    /// <returns>The server-chosen staging key.</returns>
    public static string StagingKey(Guid org, Guid artifactId) =>
        string.Create(CultureInfo.InvariantCulture, $"pending/{org:D}/{artifactId:D}");
}
