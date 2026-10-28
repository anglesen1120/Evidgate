using System.Security.Cryptography;
using Evidgate.BuildingBlocks.ObjectStorage;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// The <see cref="S3ObjectStorage.GetAsync"/> member against the real SeaweedFS container (06.3.6-T1.4): the
/// <c>parse</c> handler reads one whole artifact from the store with the byte cap its kind policy allows, so the read
/// must answer the complete object when it fits, an empty array for a zero-byte object, <c>null</c> for a missing key
/// (the 404-is-an-outcome rule of <see cref="S3ObjectStorage.HeadAsync"/>) and a failure — never a truncated array —
/// for an object above the caller's cap.
/// </summary>
[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class ObjectStorageReadTests(SeaweedFsFixture fixture) : IDisposable
{
    private const string ContentLengthHeaderName = "Content-Length";

    private readonly SeaweedFsFixture _fixture = fixture;

    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    /// <summary>A final-key read returns exactly the stored bytes when they fit the cap.</summary>
    [Fact]
    public async Task Get_Of_An_Object_Within_The_Cap_Returns_Every_Byte()
    {
        var body = RandomNumberGenerator.GetBytes(2048);
        var key = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var bytes = await adapter.GetAsync(key, body.LongLength, CancellationToken.None);

        bytes.ShouldNotBeNull();
        bytes!.ShouldBe(body, "the whole object must come back, in one array");
    }

    /// <summary>
    /// An object big enough that the response body arrives over several reads still comes back whole and exact: the
    /// read fills one buffer of the store's own declared length, so a body split across reads must not be truncated at
    /// the first one, and the answer's length is the object's length — no padding and no second copy
    /// (06.3.6-T1.4, S125 performance review 1).
    /// </summary>
    [Fact]
    public async Task Get_Of_An_Object_Arriving_In_Several_Reads_Returns_Every_Byte()
    {
        var body = RandomNumberGenerator.GetBytes(512 * 1024);
        var key = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var bytes = await adapter.GetAsync(key, body.LongLength, CancellationToken.None);

        bytes.ShouldNotBeNull();
        bytes!.Length.ShouldBe(body.Length, "the answer is exactly the object's length");
        bytes.ShouldBe(body, "a body split across reads must arrive intact and in order");
    }

    /// <summary>
    /// A store object above the caller's cap is refused instead of truncated: a truncated report is a different
    /// document, and parsing it as if it were the stored one would hide the inconsistency (06.3.6 AC3).
    /// </summary>
    [Fact]
    public async Task Get_Of_An_Object_Over_The_Cap_Throws_And_Names_No_Key()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var key = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.GetAsync(key, 1024, CancellationToken.None));

        exception.Message.ShouldContain("1024", Case.Sensitive, "the message names the cap it refused");
        exception.Message.ShouldNotContain(key, Case.Sensitive, "no key ever reaches a message (00 §5.7 S3)");
    }

    /// <summary>A missing key is an outcome, not an exception — the same rule as <c>HEAD</c>.</summary>
    [Fact]
    public async Task Get_Of_A_Missing_Key_Is_Null()
    {
        using var adapter = Adapter();

        var bytes = await adapter.GetAsync(FinalKey(), 1024, CancellationToken.None);

        bytes.ShouldBeNull("a store 404 is an outcome, not an exception");
    }

    /// <summary>A zero-byte object exists and has no bytes: the store's 416 answers an empty array.</summary>
    [Fact]
    public async Task Get_Of_A_ZeroByte_Object_Is_An_Empty_Array()
    {
        var key = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, 0, Sha256Hex([]))), []);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT of an empty body must be accepted; status={(int)response.StatusCode}");

        var bytes = await adapter.GetAsync(key, 1024, CancellationToken.None);

        bytes.ShouldNotBeNull();
        bytes.ShouldBeEmpty();
    }

    /// <summary>
    /// The promoted final key is what a <c>ready</c> row names, so the parse reads the copy, never the staging object
    /// (06.1.2-T1 decision 7).
    /// </summary>
    [Fact]
    public async Task Get_Of_A_Final_Key_After_A_Copy_Returns_The_Promoted_Bytes()
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

        var bytes = await adapter.GetAsync(final, body.LongLength, CancellationToken.None);

        bytes.ShouldNotBeNull();
        bytes!.ShouldBe(body);
    }

    /// <summary>The cap is bounded before a request leaves the process (1 .. 256 MiB).</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(268_435_457L)]
    public async Task Get_Refuses_A_Cap_Outside_The_Bound(long maxBytes)
    {
        using var adapter = Adapter();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => adapter.GetAsync(FinalKey(), maxBytes, CancellationToken.None));
    }

    /// <summary>A key with traversal is refused before any request leaves the process.</summary>
    [Fact]
    public async Task Get_Refuses_A_Key_With_Traversal()
    {
        using var adapter = Adapter();

        await Should.ThrowAsync<ArgumentException>(
            () => adapter.GetAsync("org/../etc/passwd", 1024, CancellationToken.None));
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
