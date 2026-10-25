using Evidgate.Modules.Identity.Domain;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Qa;

/// <summary>
/// QA pass over the two pure rules 02.3.1-T2 introduced (D-S81-Q2 alias fold, D-S81-Q3 domain
/// allowlist): the address forms the author's fold table does not reach and the admin-written list
/// entries a real form can produce. Nothing here needs the database, and several cases are
/// characterisation of behaviour the plan does not state, so a change of that behaviour fails loudly
/// instead of passing silently.
/// </summary>
[Trait("Category", "Docker")]
public sealed class QaAliasAndAllowlistEdgeTests
{
    /// <summary>An allowlist naming one plain domain, as an admin form would store it.</summary>
    private static readonly string[] ExampleCom = ["example.com"];

    /// <summary>The same domain written with the trailing DNS root dot.</summary>
    private static readonly string[] TrailingDotEntry = ["example.com."];

    /// <summary>A list whose only entry is whitespace (a form artefact, not a domain).</summary>
    private static readonly string[] BlankEntry = ["   "];

    /// <summary>A wildcard entry, which an admin might expect to mean "any subdomain".</summary>
    private static readonly string[] WildcardEntry = ["*.example.com"];

    /// <summary>A punycoded internationalised domain, which is what a provider reports.</summary>
    private static readonly string[] PunycodeEntry = ["xn--bcher-kva.example"];

    /// <summary>A subdomain named exactly, the only form that admits its addresses.</summary>
    private static readonly string[] SubdomainEntry = ["sub.example.com"];

    // ------------------------------------------------------------------ the alias fold

    /// <summary>
    /// A trailing root dot is not folded away, so <c>a@example.com.</c> is a different string from
    /// <c>a@example.com</c> for the ceiling. Harmless in practice (<see cref="InviteEmail"/>'s shape
    /// refuses the dotted form before it can be stored), but pinned so the gap is visible.
    /// </summary>
    [Fact]
    public void Canonical_KeepsATrailingDotDomainDistinct()
    {
        EmailAlias.Canonical("user@example.com.").ShouldBe("user@example.com.");
        EmailAlias.Canonical("user@example.com.")
            .ShouldNotBe(EmailAlias.Canonical("user@example.com"));
    }

    /// <summary>
    /// The fold is a pure string operation: a non-ASCII local part is only lower-cased, never folded
    /// onto an ASCII spelling, so an IDN local part stays its own mailbox.
    /// </summary>
    [Fact]
    public void Canonical_LowerCasesANonAsciiAddressWithoutFoldingIt()
    {
        EmailAlias.Canonical("Ünïcode@Example.COM").ShouldBe("ünïcode@example.com");
        EmailAlias.Canonical("ünïcode+tag@example.com").ShouldBe("ünïcode@example.com");
    }

    /// <summary>
    /// A Gmail local part made only of dots would lose its whole local part in the dot strip, so the
    /// fold returns the normalised address instead of an empty-local-part string.
    /// </summary>
    [Fact]
    public void Canonical_OfADotOnlyGmailLocalPart_DoesNotProduceAnEmptyLocalPart()
    {
        EmailAlias.Canonical(".@gmail.com").ShouldBe(".@gmail.com");
        EmailAlias.Canonical("..@googlemail.com").ShouldBe("..@googlemail.com");
    }

    /// <summary>A tag inside the domain part is not a sub-address: only the local part is cut.</summary>
    [Fact]
    public void Canonical_CutsTheTagInTheLocalPartOnly()
    {
        EmailAlias.Canonical("user@ex+ample.com").ShouldBe("user@ex+ample.com");
    }

    /// <summary>Every edge form above is idempotent, which the ceiling relies on (it compares two results).</summary>
    /// <param name="address">The address to fold twice.</param>
    [Theory]
    [InlineData("user@example.com.")]
    [InlineData("Ünïcode@Example.COM")]
    [InlineData(".@gmail.com")]
    [InlineData("..@googlemail.com")]
    [InlineData("user@ex+ample.com")]
    [InlineData("+tag@example.com")]
    public void Canonical_IsIdempotentForTheEdgeForms(string address)
    {
        var once = EmailAlias.Canonical(address);

        EmailAlias.Canonical(once).ShouldBe(once);
    }

    // ------------------------------------------------------------------ the allowlist

    /// <summary>
    /// A list entry written with a trailing DNS root dot is the same domain as the plain one, so it admits the
    /// plain address — and the same goes for an address the provider reports in the fully qualified form:
    /// <c>example.com.</c> and <c>example.com</c> are one domain written two ways, and treating them as
    /// different strings locked an org out of every Google bind over one admin typo (F2).
    /// </summary>
    [Fact]
    public void Admits_TreatsATrailingDotAsTheSameDomainOnEitherSide()
    {
        EmailDomainAllowlist.Admits(TrailingDotEntry, "invitee@example.com").ShouldBeTrue();
        EmailDomainAllowlist.Admits(ExampleCom, "invitee@example.com.").ShouldBeTrue();
        EmailDomainAllowlist.Admits(TrailingDotEntry, "invitee@example.com.").ShouldBeTrue();
        EmailDomainAllowlist.Admits(TrailingDotEntry, "invitee@sub.example.com").ShouldBeFalse(
            "the trailing dot changes nothing about exactness");
    }

    /// <summary>
    /// An entry of a bare dot is a domain that normalises to nothing: it matches no address, and — because the
    /// admin did write something — it must not turn the list into "no restriction" either (F2).
    /// </summary>
    [Fact]
    public void Admits_OfADotOnlyEntry_AdmitsNothingAndStillRestricts()
    {
        string[] dotOnly = ["."];

        EmailDomainAllowlist.Admits(dotOnly, "invitee@example.com").ShouldBeFalse();
        EmailDomainAllowlist.Admits(dotOnly, "invitee@.").ShouldBeFalse();
        EmailDomainAllowlist.Admits([".", "example.com"], "invitee@example.com").ShouldBeTrue(
            "the usable entry of the same list still admits its own domain");
        EmailDomainAllowlist.Admits(["."], "invitee@example").ShouldBeFalse(
            "an address without a domain is not admitted by a restricted list");
    }

    /// <summary>A list of blank entries restricts nothing, exactly like an empty list.</summary>
    [Fact]
    public void Admits_TreatsBlankEntriesAsNoRestriction()
    {
        EmailDomainAllowlist.Admits(BlankEntry, "invitee@example.com").ShouldBeTrue();
        EmailDomainAllowlist.Admits([], "invitee@example.com").ShouldBeTrue();
        EmailDomainAllowlist.Admits(null, "invitee@example.com").ShouldBeTrue();
    }

    /// <summary>
    /// An address without a domain is admitted only by a list that restricts nothing: a restricted org
    /// refuses it (the address cannot be on the list), and the unrestricted org never asked.
    /// </summary>
    [Fact]
    public void Admits_RefusesADomainlessAddressOnlyWhenTheListRestricts()
    {
        EmailDomainAllowlist.Admits(ExampleCom, "not-an-address").ShouldBeFalse();

        // An empty local part is not this rule's job: the domain matches, and the address shape is
        // refused upstream by InviteEmail.IsValid before the binder is ever called.
        EmailDomainAllowlist.Admits(ExampleCom, "@example.com").ShouldBeTrue();
        EmailDomainAllowlist.Admits(null, "not-an-address").ShouldBeTrue();
    }

    /// <summary>An exact subdomain entry admits its own addresses; the parent entry never does.</summary>
    [Fact]
    public void Admits_MatchesLabelsExactly()
    {
        EmailDomainAllowlist.Admits(SubdomainEntry, "invitee@sub.example.com").ShouldBeTrue();
        EmailDomainAllowlist.Admits(ExampleCom, "invitee@sub.example.com").ShouldBeFalse();
        EmailDomainAllowlist.Admits(SubdomainEntry, "invitee@example.com").ShouldBeFalse();
    }

    /// <summary>
    /// A wildcard entry is not a pattern this rule understands: it matches nothing, so a list of
    /// <c>*.example.com</c> admits no address at all.
    /// </summary>
    [Fact]
    public void Admits_DoesNotInterpretAWildcardEntry()
    {
        EmailDomainAllowlist.Admits(WildcardEntry, "invitee@team.example.com").ShouldBeFalse();
        EmailDomainAllowlist.Admits(WildcardEntry, "invitee@*.example.com").ShouldBeTrue();
    }

    /// <summary>A punycoded entry matches its own punycoded domain and nothing that merely ends in it.</summary>
    [Fact]
    public void Admits_MatchesPunycodeExactly()
    {
        EmailDomainAllowlist.Admits(PunycodeEntry, "invitee@xn--bcher-kva.example").ShouldBeTrue();
        EmailDomainAllowlist.Admits(["example"], "invitee@xn--bcher-kva.example").ShouldBeFalse();
    }

    /// <summary>The comparison is case-insensitive on both sides, whichever way the caller normalised.</summary>
    [Fact]
    public void Admits_IsCaseInsensitiveOnBothSides()
    {
        EmailDomainAllowlist.Admits(["Example.COM"], "invitee@example.com").ShouldBeTrue();
        EmailDomainAllowlist.Admits(ExampleCom, "invitee@EXAMPLE.com").ShouldBeTrue();
    }
}
