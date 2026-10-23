using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Contracts;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The single implementation of the 04.7.1 negotiation rules (<c>contracts/adapter/README.md</c>
/// §Negotiation rules): a pure function over (document, server protocol) with no I/O, no clock and no
/// state, so the registration path (04.7.1-T2.2) and the 05.7.x claim path evaluate a version skew with
/// the same code. Registered as a singleton <see cref="IProtocolNegotiator"/> by
/// <c>AddIngestionModule</c>.
/// </summary>
internal sealed class ProtocolNegotiator : IProtocolNegotiator
{
    /// <summary>
    /// The <c>supports</c> flags protocol v1.0 defines. Every other flag is unknown: it is listed in
    /// <see cref="NegotiationResult.UnknownSupportsFlags"/>, never evaluated, and stored verbatim (AC3).
    /// </summary>
    private static readonly HashSet<string> KnownSupports = new(StringComparer.Ordinal)
    {
        "discovery",
        "step_events",
        "retry",
        "cancel",
        "capture_control",
    };

    /// <inheritdoc />
    public NegotiationResult Negotiate(CapabilityDocument document, ServerProtocol server)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(server);

        if (!server.SupportedMajors.Contains(document.Protocol.Major))
        {
            // The rejection names the adapter's major and the server's supported majors, and nothing else
            // from the document (00 §9.2 details, AC2).
            return new NegotiationResult(
                Accepted: false,
                RejectionCode: ApiErrorCode.UnsupportedProtocolMajor,
                Details:
                [
                    new NegotiationDetail("/protocol/major", $"adapter_major={document.Protocol.Major}"),
                    new NegotiationDetail(
                        "/protocol/major",
                        $"supported_majors={string.Join(',', server.SupportedMajors.Order())}"),
                ],
                Minor: null,
                UnknownSupportsFlags: []);
        }

        var minor = document.Protocol.Minor == server.Minor
            ? MinorRelation.Equal
            : document.Protocol.Minor > server.Minor ? MinorRelation.AdapterNewer : MinorRelation.AdapterOlder;

        var unknown = document.Supports.Keys
            .Where(flag => !KnownSupports.Contains(flag))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new NegotiationResult(true, null, [], minor, unknown);
    }
}
