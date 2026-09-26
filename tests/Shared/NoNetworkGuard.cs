using System.Net;
using Shouldly;
using Xunit;

// 01.6.1-T1.1: the assembly fixture is declared here (not in the project) so that every
// *.UnitTests project picks up the guard through tests/Directory.Build.props alone.
[assembly: AssemblyFixture(typeof(Evidgate.Testing.NoNetworkGuard))]

namespace Evidgate.Testing;

/// <summary>
/// 01.6.1-T1.1 / 00 §5.6 W9: the no-network default for <c>*.UnitTests</c> assemblies.
/// It arms before the first test of the assembly runs (assembly fixture) and stays armed
/// for the whole process.
/// <para>
/// What it catches: every <see cref="HttpClient"/> built on its default handler. Its
/// process default proxy is set to a black hole on the reserved loopback port 1, and the
/// proxy bypasses nothing — including <c>localhost</c>/<c>127.0.0.1</c> (both measured,
/// 01.6.1-T1.1) — so such a request dies with <see cref="HttpRequestException"/>
/// ("Connection refused (127.0.0.1:1)") instead of reaching the network or a local test
/// server. <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>/<c>ALL_PROXY</c> (upper and lower case)
/// are pinned to the same black hole with <c>NO_PROXY</c> emptied, so a child process that
/// honours the environment instead (a Node CLI, curl, git) is covered as well.
/// </para>
/// <para>
/// What it deliberately does not catch: raw <c>Socket</c>/<c>Dns</c> calls,
/// ADO.NET/Npgsql connections, and an <c>HttpClient</c> built on a handler with
/// <c>UseProxy = false</c> / <c>Proxy = null</c> (it bypasses the process default
/// proxy entirely). No managed hook intercepts those, so they stay forbidden by
/// review; the isolation and container suites that need them are
/// <c>*.IntegrationTests</c>, which do not get this file. A unit test that opens a raw
/// socket therefore still has to be caught in review.
/// </para>
/// </summary>
public sealed class NoNetworkGuard : IAsyncLifetime
{
    /// <summary>Reserved loopback port; nothing is ever served there, so connections are refused locally.</summary>
    internal const string BlackholeProxy = "http://127.0.0.1:1/";

    private const string ArmedKey = "Evidgate.Testing.NoNetworkGuard.Armed";

    /// <summary>True once the assembly fixture ran; <see cref="NoNetworkConventionTests"/> asserts it.</summary>
    internal static bool IsArmed => (bool?)AppContext.GetData(ArmedKey) == true;

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        AppContext.SetData(ArmedKey, true);
        HttpClient.DefaultProxy = new WebProxy(BlackholeProxy);

        foreach (var variable in (string[])["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"])
        {
            Environment.SetEnvironmentVariable(variable, BlackholeProxy);
        }

        // Empty NO_PROXY: the environment-proxy path bypasses nothing either.
        foreach (var variable in (string[])["NO_PROXY", "no_proxy"])
        {
            Environment.SetEnvironmentVariable(variable, string.Empty);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 01.6.1-T1.1: the guard is invisible unless something proves it is wired up, so every
/// <c>*.UnitTests</c> assembly runs these three checks. All are I/O-free and fail when the
/// assembly fixture does not run: <c>Guard_Is_Armed</c> reads the AppContext flag set by
/// <see cref="NoNetworkGuard.InitializeAsync"/>, <c>Http_Requests_Route_To_The_Blackhole</c>
/// reads the process default proxy that SocketsHttpHandler consults for a handler-less
/// <see cref="HttpClient"/> — with no guard that is the real system/environment proxy, so the
/// assertion fails — and <c>Environment_Proxies_Route_To_The_Blackhole</c> reads the
/// <c>*_PROXY</c> variables a child process would inherit.
/// </summary>
public sealed class NoNetworkConventionTests
{
    [Fact]
    public void Guard_Is_Armed() => NoNetworkGuard.IsArmed.ShouldBeTrue();

    [Fact]
    public void Http_Requests_Route_To_The_Blackhole()
    {
        var proxy = HttpClient.DefaultProxy;

        proxy.GetProxy(new Uri("https://example.com/")).ShouldBe(new Uri(NoNetworkGuard.BlackholeProxy));
        proxy.IsBypassed(new Uri("https://example.com/")).ShouldBeFalse();
        proxy.IsBypassed(new Uri("http://localhost:1234/")).ShouldBeFalse();
        proxy.IsBypassed(new Uri("http://127.0.0.1:1234/")).ShouldBeFalse();
    }

    [Fact]
    public void Environment_Proxies_Route_To_The_Blackhole()
    {
        foreach (var variable in (string[])["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"])
        {
            Environment.GetEnvironmentVariable(variable).ShouldBe(NoNetworkGuard.BlackholeProxy);
        }

        Environment.GetEnvironmentVariable("NO_PROXY").ShouldBeEmpty();
        Environment.GetEnvironmentVariable("no_proxy").ShouldBeEmpty();
    }
}
