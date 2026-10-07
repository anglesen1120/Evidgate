namespace Evidgate.Modules.Ingestion.Contracts;

/// <summary>
/// How a producer's results may be used (00 §10, D7; 04.7.1 AC4/AC7). The wire values are
/// <c>full</c> and <c>ingest_only</c> (<c>contracts/adapter/capability.v1.json</c>).
/// </summary>
public enum AdapterMode
{
    /// <summary>The adapter's results may gate a decision.</summary>
    Full,

    /// <summary>Results are stored and viewable but never gate-eligible unless the manifest is proven complete.</summary>
    IngestOnly,
}

/// <summary>The protocol version an adapter speaks (<c>protocol.major</c>/<c>protocol.minor</c>, 00 §10).</summary>
/// <param name="Major">The major version; a different major is a breaking change (a new protocol, not an edit).</param>
/// <param name="Minor">The minor version; a newer adapter minor is accepted, an older one is accepted for the operations it declares.</param>
public sealed record ProtocolVersion(int Major, int Minor);

/// <summary>
/// The protocol versions this server speaks: the majors it accepts and the minor it implements
/// (04.7.1-T2.1). The negotiator is the single implementation of the rules, so the registration path
/// (04.7.1-T2.2) and the 05.7.x claim path evaluate a version skew with the same code.
/// </summary>
/// <param name="SupportedMajors">The majors the server accepts; an adapter major outside this set is rejected.</param>
/// <param name="Minor">The minor the server implements.</param>
public sealed record ServerProtocol(IReadOnlyCollection<int> SupportedMajors, int Minor)
{
    /// <summary>
    /// The server's protocol today: major <c>1</c>, minor <c>0</c> — the only version defined
    /// (<c>contracts/adapter/README.md</c> §Negotiation rules).
    /// </summary>
    public static ServerProtocol Current { get; } = new([1], 0);
}

/// <summary>
/// A capability document bound from a <c>capability.v1</c>-validated JSON body (04.7.1-T2.2): the
/// producer-level metadata the negotiator and the store read. <see cref="Supports"/> keeps every flag the
/// document declared — a flag this server does not know is never evaluated and is stored verbatim (AC3).
/// </summary>
/// <param name="Protocol">The protocol version the adapter speaks.</param>
/// <param name="EngineFamily">The engine family, e.g. <c>playwright</c>.</param>
/// <param name="EngineVersion">The exact engine version (D8: never resolved as <c>latest</c> at run time).</param>
/// <param name="Platforms">The platforms the adapter runs on.</param>
/// <param name="Supports">Every declared support flag, known or not.</param>
/// <param name="Mode">Whether the producer's results may gate a decision.</param>
public sealed record CapabilityDocument(
    ProtocolVersion Protocol,
    string EngineFamily,
    string EngineVersion,
    IReadOnlyList<string> Platforms,
    IReadOnlyDictionary<string, bool> Supports,
    AdapterMode Mode);

/// <summary>How an adapter's minor version relates to the server's, within one major (04.7.1 AC3).</summary>
public enum MinorRelation
{
    /// <summary>The adapter speaks the server's minor.</summary>
    Equal,

    /// <summary>The adapter's minor is higher: accepted, and its newer support flags are unknown to this server.</summary>
    AdapterNewer,

    /// <summary>The adapter's minor is lower: accepted for the operations that minor declares.</summary>
    AdapterOlder,
}

/// <summary>One reason a capability document was refused, in the 00 §9.2 <c>details[]</c> shape.</summary>
/// <param name="Path">JSON pointer to the offending member.</param>
/// <param name="Reason">Machine-readable reason; never a value from the document.</param>
public sealed record NegotiationDetail(string Path, string Reason);

/// <summary>
/// The outcome of one negotiation (04.7.1-T2.1): accepted, or rejected with the 00 §9.2 code and details.
/// A rejection names the adapter's major and the server's supported majors and nothing else from the
/// document (AC2).
/// </summary>
/// <param name="Accepted">True when the document may be registered.</param>
/// <param name="RejectionCode">The 00 §9.2 code when rejected, else null.</param>
/// <param name="Details">The rejection reasons; empty when accepted.</param>
/// <param name="Minor">The minor relation when accepted, else null.</param>
/// <param name="UnknownSupportsFlags">The declared flags this server does not know, sorted; never evaluated.</param>
public sealed record NegotiationResult(
    bool Accepted,
    string? RejectionCode,
    IReadOnlyList<NegotiationDetail> Details,
    MinorRelation? Minor,
    IReadOnlyList<string> UnknownSupportsFlags);

/// <summary>
/// The pure negotiation rule (04.7.1-T2.1, 00 §10): a function over (document, server protocol) with no
/// I/O, so the registration path and the 05.7.x claim path share one verdict. Exported through this
/// project so another module reaches it without a module-to-module reference (00 §5.5).
/// </summary>
public interface IProtocolNegotiator
{
    /// <summary>Negotiates <paramref name="document"/> against <paramref name="server"/>.</summary>
    /// <param name="document">The bound capability document.</param>
    /// <param name="server">The protocol versions the server speaks.</param>
    /// <returns>The verdict; a rejection carries the 00 §9.2 code and details.</returns>
    NegotiationResult Negotiate(CapabilityDocument document, ServerProtocol server);
}

/// <summary>
/// The gate-eligibility rule of an adapter mode (00 §10, D7; 04.7.1 AC4): an <c>ingest_only</c> producer's
/// results are stored and viewable but can never produce a gating PASS unless the manifest is proven
/// complete. 06.2.2 reads this when it derives the gate-eligibility flag of a revision.
/// </summary>
public static class AdapterModeRules
{
    /// <summary>True when a run produced by an adapter in <paramref name="mode"/> may gate a decision.</summary>
    /// <param name="mode">The producer's declared mode.</param>
    /// <param name="manifestProvenComplete">True when the run's manifest is proven complete (00 §10).</param>
    public static bool IsGateEligible(AdapterMode mode, bool manifestProvenComplete) =>
        mode == AdapterMode.Full || manifestProvenComplete;
}
