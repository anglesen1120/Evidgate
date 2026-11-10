using System.Net;
using Evidgate.Modules.Identity.Ci;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Forwarding;

public sealed class ClientKeyTests
{
    [Fact]
    public void An_IPv4_Mapped_IPv6_Address_Shares_The_Key_Of_Its_IPv4_Form()
    {
        var plain = ExchangeRateLimiter.ClientKey(IPAddress.Parse("203.0.113.7"));
        var mapped = ExchangeRateLimiter.ClientKey(IPAddress.Parse("::ffff:203.0.113.7"));

        mapped.ShouldBe(plain);
    }

    [Fact]
    public void Addresses_Of_One_IPv6_Subscriber_Prefix_Share_A_Key_And_Another_Prefix_Does_Not()
    {
        var first = ExchangeRateLimiter.ClientKey(IPAddress.Parse("2001:db8:1:2::1"));
        var sameSlash64 = ExchangeRateLimiter.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff::9"));
        var other = ExchangeRateLimiter.ClientKey(IPAddress.Parse("2001:db8:1:3::1"));

        sameSlash64.ShouldBe(first);
        other.ShouldNotBe(first);
    }

    [Fact]
    public void A_Scoped_IPv6_Address_Keys_Like_The_Unscoped_One()
    {
        var scoped = IPAddress.Parse("fe80::1");
        scoped.ScopeId = 6;

        ExchangeRateLimiter.ClientKey(scoped).ShouldBe(ExchangeRateLimiter.ClientKey(IPAddress.Parse("fe80::1")));
    }
}
