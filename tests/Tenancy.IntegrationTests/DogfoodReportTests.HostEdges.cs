using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    /// <summary>A host name with surrounding whitespace is refused exactly like the trimmed name.</summary>
    [Theory]
    [InlineData("  Runner-Zq", "see Runner-Zq")]
    [InlineData("Runner-Zq\r\n", "see Runner-Zq")]
    [InlineData("\t build-box \t", "see build-box")]
    [InlineData(" ci ", "a ci b")]
    public async Task Dogfood_Collect_HostNameWithSurroundingWhitespace_IsRefusedLikeTheTrimmedName(string hostname, string text) =>
        await AssertRefusedAsync(Report(text), hostname: hostname);

    /// <summary>An all-whitespace host name falls back like an empty one: it refuses nothing.</summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("")]
    public async Task Dogfood_Collect_AllWhitespaceHostName_BehavesAsEmpty(string hostname) =>
        await AssertAcceptedHostAsync(hostname, "a b whitespace c");

    /// <summary>The full name localhost identifies nobody: localhost URLs pass, whatever its case or padding.</summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData(" localhost\r\n")]
    public async Task Dogfood_Collect_LocalhostHostName_DoesNotRefuseLocalhostUrls(string hostname) =>
        await AssertAcceptedHostAsync(hostname, "http://localhost:4200 and http://LOCALHOST:5000");

    /// <summary>Every host name, short or long, refuses itself in a name and leaves a neutral control alone.</summary>
    [Theory]
    [InlineData("out")]
    [InlineData("x")]
    [InlineData("ci")]
    [InlineData("Injected-Long-Runner-Host-Zq")]
    public async Task Dogfood_Collect_InjectedHostName_RefusesItselfAndNotTheControl(string hostname)
    {
        await AssertAcceptedHostAsync(hostname, "safe");
        await AssertRefusedAsync(Report("see " + hostname + " now"), hostname: hostname);
    }

    private static async Task AssertAcceptedHostAsync(string hostname, string text)
    {
        await WithReports(Report(text), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, hostname: hostname);
            result.Exit.ShouldBe(0, result.Stderr);
        });
    }
}
