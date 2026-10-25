using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Completion;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T1.4 / hand-over (vi) of #160 (<c>ClientAddressTests</c>): the client address that reaches
/// <c>session.ip</c> is always an <c>inet</c> literal or nothing.
/// </summary>
/// <remarks>
/// Pure unit cases over the real type: the integration suite proves the wiring once, and these pin the
/// boundaries — a zone NAME (which the parser refuses), a zone INDEX (which it accepts but <c>inet</c> does
/// not), an IPv4 with a port-shaped suffix, and hostile text that must never be echoed into the row.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ClientAddressTests
{
    /// <summary>A zone index is dropped, so the literal is exactly what <c>inet</c> accepts.</summary>
    [Theory]
    [InlineData("fe80::1%6", "fe80::1")]
    [InlineData("fe80::1%eth0", "fe80::1")]
    [InlineData("fe80::1", "fe80::1")]
    public void A_Zone_Index_Is_Dropped(string address, string expected)
    {
        ClientAddress.Sanitise(address).ShouldBe(expected);
    }

    /// <summary>An ordinary address is canonicalised, never reformatted into something the column rejects.</summary>
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData(" 203.0.113.7 ", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::ffff:203.0.113.7", "::ffff:203.0.113.7")]
    public void A_Valid_Address_Is_Canonicalised(string address, string expected)
    {
        ClientAddress.Sanitise(address).ShouldBe(expected);
    }

    /// <summary>Anything that is not an address is dropped rather than echoed into the session row.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("203.0.113.7:443")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("fe80::1%")]
    public void Anything_Else_Is_Not_Stored(string? address)
    {
        ClientAddress.Sanitise(address).ShouldBeNull();
    }
}

/// <summary>
/// 02.3.1-T1.4 / hand-over (vi) of #160 (<c>ReturnUrlEncoderTests</c>): the encoded <c>returnUrl</c> is an ASCII
/// app path, never a double-encoded one and never an open redirect.
/// </summary>
/// <remarks>
/// The allowlist (<see cref="ReturnUrlPolicy"/>) runs first; this type only makes the approved value safe to
/// carry in a header. The cases pin the three properties that matter: non-ASCII is percent-encoded as UTF-8, an
/// existing <c>%XX</c> escape is NOT encoded again, and anything the second allowlist pass refuses falls back to
/// the app root instead of throwing.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ReturnUrlEncoderTests
{
    /// <summary>An ASCII app path is returned unchanged.</summary>
    [Theory]
    [InlineData("/runs/42")]
    [InlineData("/")]
    [InlineData("/runs/42?tab=steps#top")]
    public void An_Ascii_App_Path_Is_Unchanged(string returnUrl)
    {
        ReturnUrlEncoder.ToLocation(returnUrl).ShouldBe(returnUrl);
    }

    /// <summary>Non-ASCII path characters are percent-encoded as UTF-8, so the header stays ASCII.</summary>
    [Theory]
    [InlineData("/runs/café", "/runs/caf%C3%A9")]
    [InlineData("/runs/日本語", "/runs/%E6%97%A5%E6%9C%AC%E8%AA%9E")]
    [InlineData("/runs/🚀", "/runs/%F0%9F%9A%80")]
    public void Non_Ascii_Is_Encoded_Once(string returnUrl, string expected)
    {
        var encoded = ReturnUrlEncoder.ToLocation(returnUrl);

        encoded.ShouldBe(expected);
        encoded.All(character => character < 128).ShouldBeTrue("a Location header value is ASCII");
    }

    /// <summary>An escape the client already wrote is kept, never encoded again.</summary>
    [Theory]
    [InlineData("/runs/a%20b")]
    [InlineData("/runs/caf%C3%A9")]
    public void An_Existing_Escape_Is_Not_Double_Encoded(string returnUrl)
    {
        ReturnUrlEncoder.ToLocation(returnUrl).ShouldBe(returnUrl);
    }

    /// <summary>Empty input, or input the second allowlist pass refuses, is the app root — never a throw.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example/landing")]
    public void A_Refused_Value_Is_The_App_Root(string returnUrl)
    {
        var encoded = ReturnUrlEncoder.ToLocation(returnUrl);

        encoded.ShouldBe("/");
        ReturnUrlPolicy.IsAppPath(encoded).ShouldBeTrue("the app root is always an app path");
    }

    /// <summary>AC3 of the envelope: the two new completion refusals are distinct 403 codes.</summary>
    [Fact]
    public void The_Completion_Refusals_Are_Distinct_Error_Codes()
    {
        ApiErrorCode.NoMembership.ShouldBe("no_membership");
        ApiErrorCode.AccountDisabled.ShouldBe("account_disabled");
    }
}
