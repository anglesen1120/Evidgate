using System.Net;
using System.Net.Sockets;
using Evidgate.Modules.Policy.Webhooks.Application;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed class WebhookUrlGuardTests
{
    private const string PublicUrl = "https://receiver.example/hook";
    private static readonly IPAddress PublicIp = IPAddress.Parse("1.1.1.1");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://receiver.example/")]
    [InlineData("ftp://receiver.example/")]
    [InlineData("https://good.com@10.0.0.1/")]
    [InlineData("https://user@receiver.example/")]
    [InlineData("https://@receiver.example/")]
    [InlineData("https://receiver.example:80/")]
    [InlineData("https://receiver.example:8443/")]
    [InlineData("https://receiver.example:65535/")]
    [InlineData("localhost")]
    public async Task EndpointUrl_SchemeUserinfoPort_Rejected(string url)
    {
        var guard = Guard(PublicIp);
        (await guard.CheckAsync(url, Token)).Allowed.ShouldBeFalse();
        (await guard.CheckAsync(PublicUrl, Token)).Allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("service.localhost.")]
    [InlineData("host.internal")]
    [InlineData("printer.local")]
    [InlineData("intranet.")]
    [InlineData("127.0.0.1.")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("0177.0.0.1")]
    [InlineData("127.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("192.0.2.42")]
    [InlineData("192.0.0.6")]
    [InlineData("198.51.100.42")]
    [InlineData("203.0.113.42")]
    [InlineData("198.19.0.1")]
    [InlineData("[::]")]
    [InlineData("[::1]")]
    [InlineData("[::127.0.0.1]")]
    [InlineData("[::ffff:7f00:1]")]
    [InlineData("[::ffff:169.254.169.254]")]
    [InlineData("[64:ff9b::808:808]")]
    [InlineData("[64:ff9b:1::1]")]
    [InlineData("[fc00::1]")]
    [InlineData("[fdff::1]")]
    [InlineData("[fe80::42]")]
    [InlineData("[ff02::1]")]
    [InlineData("[2002:0808:0808::1]")]
    [InlineData("[2001:0::1]")]
    [InlineData("[2001:2::1]")]
    [InlineData("[2001:db8::1]")]
    public async Task EndpointUrl_PrivateRanges_Rejected(string host)
    {
        var guard = Guard(PublicIp);
        (await guard.CheckAsync($"https://{host}/", Token)).Allowed.ShouldBeFalse();
        (await guard.CheckAsync("https://1.1.1.1/", Token)).Allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://receiver.example/", WebhookUrlReasons.Scheme)]
    [InlineData("https://u@receiver.example/", WebhookUrlReasons.Userinfo)]
    [InlineData("https://@receiver.example/", WebhookUrlReasons.Userinfo)]
    [InlineData("https://receiver.example:8443/", WebhookUrlReasons.Port)]
    [InlineData("https://localhost/", WebhookUrlReasons.Host)]
    [InlineData("https://127.0.0.1/", WebhookUrlReasons.Host)]
    public async Task Refusal_NamesTheRuleClassOnly(string url, string reason) =>
        (await Guard(PublicIp).CheckAsync(url, Token)).Reason.ShouldBe(reason);

    [Fact]
    public async Task Delivery_MetadataAddress_Blocked()
    {
        var mixed = await Guard(PublicIp, IPAddress.Parse("169.254.169.254")).CheckAsync(PublicUrl, Token);
        mixed.Allowed.ShouldBeFalse();
        mixed.Reason.ShouldBe(WebhookUrlReasons.Address);
        (await Guard(PublicIp).CheckAsync(PublicUrl, Token)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task AllowedPort_PublicAnswers_ReturnsOnlyTheValidatedAddresses()
    {
        IPAddress[] addresses = [PublicIp, IPAddress.Parse("2606:4700:4700::1111")];
        var guard = new WebhookUrlGuard(new Resolver(addresses), Options.Create(new WebhookUrlOptions { AllowedPorts = [8443] }));
        var result = (await guard.CheckAsync("https://receiver.example.:8443/hook", Token)).Target.ShouldNotBeNull();
        result.Uri.Port.ShouldBe(8443);
        result.Addresses.ShouldBe(addresses);
    }

    [Fact]
    public async Task Resolution_EmptyOrUnavailable_FailsClosed()
    {
        (await Guard().CheckAsync(PublicUrl, Token)).Reason.ShouldBe(WebhookUrlReasons.Resolution);
        var guard = new WebhookUrlGuard(new FailedResolver(), Options.Create(new WebhookUrlOptions()));
        (await guard.CheckAsync(PublicUrl, Token)).Reason.ShouldBe(WebhookUrlReasons.Resolution);
    }

    private static WebhookUrlGuard Guard(params IPAddress[] addresses) =>
        new(new Resolver(addresses), Options.Create(new WebhookUrlOptions()));

    private sealed class Resolver(IPAddress[] addresses) : IWebhookHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(addresses);
    }

    private sealed class FailedResolver : IWebhookHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
    }
}
