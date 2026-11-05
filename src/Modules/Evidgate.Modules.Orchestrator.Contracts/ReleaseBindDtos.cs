namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>One component a run's build matched: the key and the digest both sides agree on.</summary>
/// <param name="ComponentKey">The component's key inside its release.</param>
/// <param name="Digest">The build digest, <c>sha256:</c> + 64 lower-case hex. A build identifier, never a secret.</param>
public sealed record ReleaseBoundComponent(string ComponentKey, string Digest);

/// <summary>
/// One component a run could not be bound for, and why. Computed on read from the release's components and the run's
/// recorded <c>components</c> object; never stored, so a later read of the same pair derives the same answer without a
/// migration or a recency join.
/// </summary>
/// <param name="ComponentKey">The release component the run did not match.</param>
/// <param name="Reason"><c>digest_mismatch</c> (the run recorded another digest for the key) or <c>component_missing</c> (the run recorded nothing usable for it).</param>
/// <param name="ExpectedDigest">The digest the release requires.</param>
/// <param name="ActualDigest">The digest the run recorded, or null when it recorded none.</param>
public sealed record ReleaseMismatch(string ComponentKey, string Reason, string ExpectedDigest, string? ActualDigest);

/// <summary>
/// The <c>201</c> answer of <c>POST /v1/releases/{id}/runs</c>: the components bound for the pair after this call — a
/// re-bind answers the rows already stored as well — and every release component the run did not match.
/// </summary>
/// <param name="ReleaseId">The release from the route.</param>
/// <param name="RunId">The run the body named.</param>
/// <param name="Bound">Every component currently bound for the pair, ordered by component key.</param>
/// <param name="Mismatched">Every release component the run does not match, ordered by component key.</param>
public sealed record ReleaseBindResult(
    Guid ReleaseId,
    Guid RunId,
    IReadOnlyList<ReleaseBoundComponent> Bound,
    IReadOnlyList<ReleaseMismatch> Mismatched);

/// <summary>One stored <c>release_run</c> row, as the release read reports it.</summary>
/// <param name="RunId">The bound run.</param>
/// <param name="ComponentKey">The release component this row binds the run for.</param>
/// <param name="BoundAt">When the row was written.</param>
public sealed record ReleaseBoundRun(Guid RunId, string ComponentKey, DateTimeOffset BoundAt);
