using System.Text;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The tree and blob parsers of the App client against a stub handler: the truncated flag, the entry
/// and path caps, and the base64 decode with its decoded-size cap. No test opens a socket.
/// </summary>
public sealed class GitHubClientParseTests
{
    [Fact]
    public async Task Tree_TruncatedFlag_IsSurfaced()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(
            $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","truncated":true,"tree":[{{GitHubReadStubs.Entry("src/a.ts")}}]}""")));

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Truncated.ShouldBeTrue();
        result.Value.Entries.Single().Path.ShouldBe("src/a.ts");
    }

    [Fact]
    public async Task Tree_OverEntryCap_IsRefusedInsteadOfBuffered()
    {
        var bench = new GitHubTestBench(
            GitHubReadStubs.Handler(_ => GitHubResponses.Json(
                $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","truncated":false,"tree":[{{GitHubReadStubs.Entry("a")}},{{GitHubReadStubs.Entry("b")}},{{GitHubReadStubs.Entry("c")}}]}""")),
            new GitHubOptions { AppId = "12345", MaxTreeEntries = 2 });

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
    }

    [Fact]
    public async Task Tree_UnsafePath_IsRefused()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(
            $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","truncated":false,"tree":[{{GitHubReadStubs.Entry("../../etc/passwd")}}]}""")));

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
    }

    [Fact]
    public async Task Blob_IsBase64Decoded()
    {
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(
            $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","encoding":"base64","size":5,"content":"{{content}}"}""")));

        var result = await bench.Client.GetBlobAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Size.ShouldBe(5);
        Encoding.UTF8.GetString(result.Value.Content).ShouldBe("hello");
    }

    [Fact]
    public async Task Blob_OverCap_IsRefused()
    {
        var oversized = Convert.ToBase64String(new byte[100]);
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(
            $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","encoding":"base64","size":100,"content":"{{oversized}}"}""")));

        var result = await bench.Client.GetBlobAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(60)]
    [InlineData(61)]
    public async Task Blob_WrappedBase64AtTheDecodedCap_IsAccepted(int wrapWidth)
    {
        var content = GitHubReadStubs.Wrapped(Convert.ToBase64String(new byte[64]), wrapWidth);
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(
            $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","encoding":"base64","size":64,"content":"{{content.Replace("\n", "\\n", StringComparison.Ordinal)}}"}""")));

        var result = await bench.Client.GetBlobAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Content.Length.ShouldBe(64);
    }

    [Fact]
    public async Task Blob_Base64LongerThanTheDecodedCap_IsRefused()
    {
        // 67 bytes encode to 92 characters, past the 88 the 64-byte cap allows, though the declared
        // size claims 64; the cap is counted on non-whitespace characters.
        var oversized = Convert.ToBase64String(new byte[67]);
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(
            $$"""{"sha":"{{GitHubReadStubs.ObjectSha}}","encoding":"base64","size":64,"content":"{{oversized}}"}""")));

        var result = await bench.Client.GetBlobAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
    }

}
