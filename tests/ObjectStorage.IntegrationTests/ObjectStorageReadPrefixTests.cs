using System.Security.Cryptography;
using Evidgate.BuildingBlocks.ObjectStorage;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// The <see cref="S3ObjectStorage.ReadPrefixAsync"/> member against the real SeaweedFS container
/// (06.3.1-T1): the read is a ranged <c>GET</c> that returns exactly the requested prefix of a larger
/// object, all of a shorter one, and <c>null</c> for a missing key (the same 404-is-an-outcome rule as
/// <see cref="S3ObjectStorage.HeadAsync"/>). The cap is bounded (1..65536) and every key with traversal,
/// an empty segment, a backslash or a control character is refused before any request leaves the process.
/// </summary>
[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class ObjectStorageReadPrefixTests(SeaweedFsFixture fixture) : IDisposable
{
    private const string ContentLengthHeaderName = "Content-Length";

    private readonly SeaweedFsFixture _fixture = fixture;

    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task ReadPrefix_Of_A_Larger_Object_Returns_Exactly_MaxBytes()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var key = StagingKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var prefix = await adapter.ReadPrefixAsync(key, 1024, CancellationToken.None);

        prefix.ShouldNotBeNull();
        prefix!.Length.ShouldBe(1024, "the ranged GET must return exactly the requested prefix");
        prefix.ShouldBe(body[..1024]);
    }

    [Fact]
    public async Task ReadPrefix_Of_A_Smaller_Object_Returns_All_Bytes()
    {
        var body = RandomNumberGenerator.GetBytes(100);
        var key = StagingKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var prefix = await adapter.ReadPrefixAsync(key, 1024, CancellationToken.None);

        prefix.ShouldNotBeNull();
        prefix!.Length.ShouldBe(body.Length, "an object shorter than the cap is returned whole");
        prefix.ShouldBe(body);
    }

    [Fact]
    public async Task ReadPrefix_With_The_Maximum_Cap_Is_Allowed_And_Returns_The_Whole_Object()
    {
        var body = RandomNumberGenerator.GetBytes(2048);
        var key = StagingKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        // 65536 is the inclusive upper bound of the cap (IObjectStorage.ReadPrefixAsync).
        var prefix = await adapter.ReadPrefixAsync(key, 65536, CancellationToken.None);

        prefix.ShouldNotBeNull();
        prefix.ShouldBe(body);
    }

    [Fact]
    public async Task ReadPrefix_Of_A_Final_Key_After_A_Copy_Returns_The_Promoted_Bytes()
    {
        var body = RandomNumberGenerator.GetBytes(2048);
        var staging = StagingKey();
        var final = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(staging, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        await adapter.CopyAsync(staging, final, CancellationToken.None);

        // The completion's sniff reads the FINAL key, never the staging one (06.3.1-T1).
        var prefix = await adapter.ReadPrefixAsync(final, 512, CancellationToken.None);

        prefix.ShouldNotBeNull();
        prefix!.Length.ShouldBe(512);
        prefix.ShouldBe(body[..512]);
    }

    [Fact]
    public async Task ReadPrefix_Of_A_Missing_Key_Is_Null()
    {
        using var adapter = Adapter();

        var prefix = await adapter.ReadPrefixAsync(StagingKey(), 512, CancellationToken.None);

        prefix.ShouldBeNull("a store 404 is an outcome, not an exception");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    [InlineData(int.MaxValue)]
    public async Task ReadPrefix_Refuses_A_MaxBytes_Outside_The_Cap(int maxBytes)
    {
        using var adapter = Adapter();

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            adapter.ReadPrefixAsync(StagingKey(), maxBytes, CancellationToken.None));

        exception.ParamName.ShouldBe("maxBytes");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("pending/../../etc/passwd")]
    [InlineData("/pending/x")]
    [InlineData("pending//x")]
    [InlineData("pending/")]
    [InlineData("pending\\x")]
    [InlineData("pending/a\nb")]
    [InlineData("")]
    public async Task ReadPrefix_Refuses_A_Key_With_Traversal_Or_An_Empty_Segment(string key)
    {
        using var adapter = Adapter();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            adapter.ReadPrefixAsync(key, 512, CancellationToken.None));

        exception.ParamName.ShouldBe("objectKey");
        if (key.Length > 0)
        {
            exception.Message.ShouldNotContain(key, Case.Sensitive);
        }
    }

    private S3ObjectStorage Adapter() => new(Options.Create(new ObjectStorageOptions
    {
        Endpoint = _fixture.Target.ServiceUrl,
        Region = _fixture.Target.Region,
        Bucket = _fixture.Target.Bucket,
        ForcePathStyle = _fixture.Target.ForcePathStyle,
        PresignExpirySeconds = 900,
        AccessKey = _fixture.Target.AccessKey,
        SecretKey = _fixture.Target.SecretKey,
    }));

    /// <summary>The <c>pending/&lt;org_id&gt;/&lt;artifact_id&gt;</c> staging key the adapter mints (06.1.2 §5).</summary>
    private static string StagingKey() => $"pending/{Guid.NewGuid():D}/{Guid.NewGuid():D}";

    /// <summary>A final FR-06-1 key: never under <c>pending/</c>.</summary>
    private static string FinalKey() =>
        $"org/{Guid.NewGuid():D}/project/{Guid.NewGuid():D}/run/{Guid.NewGuid():D}/attempt/"
        + $"{Guid.NewGuid():N}{Guid.NewGuid():N}/1/{Guid.NewGuid():D}";

    private static string Sha256Hex(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>
    /// Sends the request a customer client sends: the signed headers verbatim, with
    /// <c>Content-Length</c> left to the content so a body of a different size produces a
    /// different header value on the wire.
    /// </summary>
    private async Task<HttpResponseMessage> PutAsync(PresignedPut presigned, byte[] body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, presigned.Url)
        {
            Content = new ByteArrayContent(body),
        };
        foreach (var (name, value) in presigned.RequiredHeaders)
        {
            if (string.Equals(name, ContentLengthHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            message.Content.Headers.TryAddWithoutValidation(name, value);
        }

        return await _http.SendAsync(message);
    }
}
