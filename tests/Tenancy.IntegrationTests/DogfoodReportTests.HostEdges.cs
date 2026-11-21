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

    /// <summary>The same names do not refuse a report that lacks them, so only the trimmed name explains the refusals above.</summary>
    [Theory]
    [InlineData("  Runner-Zq", "see Runner Zq")]
    [InlineData("\t build-box \t", "see build box")]
    public async Task Dogfood_Collect_HostNameWithWhitespace_DoesNotRefuseOtherText(string hostname, string text) =>
        await AssertAcceptedHostAsync(hostname, text);

    /// <summary>An all-whitespace host name falls back like an empty one: it refuses nothing, not even padding.</summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("")]
    [InlineData("\u200b \u200b")]
    public async Task Dogfood_Collect_AllWhitespaceHostName_BehavesAsEmpty(string hostname) =>
        await AssertAcceptedHostAsync(hostname, "a     b - c");

    /// <summary>An ignorable character cannot keep whitespace in the name after folding.</summary>
    [Fact]
    public async Task Dogfood_Collect_IgnorableCharacterBesideWhitespace_StillRefusesTheName() =>
        await AssertRefusedAsync(Report("runner-zq"), hostname: "\u200b runner-zq");

    /// <summary>Only the exact name localhost is exempt; a longer name that starts with it refuses itself.</summary>
    [Theory]
    [InlineData("localhost.corp.local", "see localhost.corp.local")]
    [InlineData("localhost1", "see localhost1")]
    public async Task Dogfood_Collect_NameBeyondLocalhost_StillRefusesItself(string hostname, string text) =>
        await AssertRefusedAsync(Report(text), hostname: hostname);

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
