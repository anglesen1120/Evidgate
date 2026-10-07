using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.7.1-T2.1 without a database: the pure negotiation rule (AC2/AC3) and the
/// gate-eligibility rule of an adapter mode (AC4). The negotiator is the one
/// implementation the registration path (T2.2/T2.3) and the 05.7.x claim path share.
/// </summary>
public sealed class ProtocolNegotiatorUnitTests
{
    private static readonly ProtocolNegotiator Negotiator = new();

    /// <summary>A bound capability document with the named support flags set true.</summary>
    private static CapabilityDocument Document(int major, int minor, params string[] supports)
    {
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var flag in supports)
        {
            flags[flag] = true;
        }

        return new CapabilityDocument(
            new ProtocolVersion(major, minor),
            "playwright",
            "1.49.0",
            ["linux-x64"],
            flags,
            AdapterMode.Full);
    }

    [Fact]
    public void Negotiate_EqualMinor_Accepts()
    {
        var result = Negotiator.Negotiate(Document(1, 0, "discovery"), ServerProtocol.Current);

        result.Accepted.ShouldBeTrue();
        result.RejectionCode.ShouldBeNull();
        result.Details.ShouldBeEmpty();
        result.Minor.ShouldBe(MinorRelation.Equal);
        result.UnknownSupportsFlags.ShouldBeEmpty();
    }

    [Fact]
    public void Negotiate_AdapterNewerMinor_AcceptsAndListsUnknownFlags()
    {
        var result = Negotiator.Negotiate(
            Document(1, 3, "discovery", "strict_mode", "shadow_dom"),
            ServerProtocol.Current);

        result.Accepted.ShouldBeTrue();
        result.RejectionCode.ShouldBeNull();
        result.Minor.ShouldBe(MinorRelation.AdapterNewer);
        // Unknown flags are listed sorted ordinal and never evaluated (AC3).
        result.UnknownSupportsFlags.ShouldBe(["shadow_dom", "strict_mode"]);
    }

    [Fact]
    public void Negotiate_AdapterOlderMinor_Accepts()
    {
        // The server implements minor 1; an adapter declaring minor 0 is accepted for
        // the operations that minor declares (AC3).
        var result = Negotiator.Negotiate(Document(1, 0, "discovery"), new ServerProtocol([1], 1));

        result.Accepted.ShouldBeTrue();
        result.RejectionCode.ShouldBeNull();
        result.Minor.ShouldBe(MinorRelation.AdapterOlder);
    }

    [Theory]
    [InlineData(2, 1)] // adapter major 2 against a server that supports {1}
    [InlineData(1, 2)] // adapter major 1 against a server that supports {2}
    public void Negotiate_MajorMismatch_Rejects(int adapterMajor, int serverMajor)
    {
        var result = Negotiator.Negotiate(
            Document(adapterMajor, 0, "discovery"),
            new ServerProtocol([serverMajor], 0));

        result.Accepted.ShouldBeFalse();
        result.RejectionCode.ShouldBe(ApiErrorCode.UnsupportedProtocolMajor);
        result.Minor.ShouldBeNull();
        result.UnknownSupportsFlags.ShouldBeEmpty();
        // The rejection names the adapter's major and the server's supported majors, and
        // nothing else from the document (AC2).
        result.Details.ShouldContain(detail => detail.Reason == $"adapter_major={adapterMajor}");
        result.Details.ShouldContain(detail => detail.Reason == $"supported_majors={serverMajor}");
        result.Details.ShouldAllBe(detail => detail.Path == "/protocol/major");
    }

    [Fact]
    public void Negotiate_MultipleServerMajors_AcceptsMember()
    {
        var result = Negotiator.Negotiate(Document(2, 0, "discovery"), new ServerProtocol([1, 2], 0));

        result.Accepted.ShouldBeTrue();
        result.Minor.ShouldBe(MinorRelation.Equal);
    }

    [Theory]
    [InlineData(AdapterMode.Full, false, true)]
    [InlineData(AdapterMode.Full, true, true)]
    [InlineData(AdapterMode.IngestOnly, false, false)]
    [InlineData(AdapterMode.IngestOnly, true, true)]
    public void AdapterModeRules_IsGateEligible_TruthTable(
        AdapterMode mode,
        bool manifestProvenComplete,
        bool expected)
    {
        AdapterModeRules.IsGateEligible(mode, manifestProvenComplete).ShouldBe(expected);
    }
}
