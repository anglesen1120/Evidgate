using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Evidgate.BuildingBlocks.ObjectStorage;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// The <see cref="S3ObjectStorage"/> <c>HEAD</c>/<c>COPY</c>/<c>DELETE</c> members against the real
/// SeaweedFS container (06.1.2-T2, T86): a <c>HEAD</c> reports the stored size and the SHA-256 the
/// presigned <c>PUT</c> bound (or <c>null</c> when the store records none), a copy from a staging key
/// to a final key preserves both and overwrites idempotently, a delete removes the object and is a
/// no-op on a missing key, and every key with traversal, an empty segment, a backslash or a control
/// character is refused before any request leaves the process.
/// </summary>
[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class ObjectStorageHeadCopyDeleteTests : IDisposable
{
    private const string ContentLengthHeaderName = "Content-Length";

    private readonly SeaweedFsFixture _fixture;
    private readonly HttpClient _http = new();

    public ObjectStorageHeadCopyDeleteTests(SeaweedFsFixture fixture) => _fixture = fixture;

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Head_Of_A_Missing_Key_Is_Null()
    {
        using var adapter = Adapter();

        var head = await adapter.HeadAsync(StagingKey(), CancellationToken.None);

        head.ShouldBeNull("a store 404 is an outcome, not an exception");
    }

    [Fact]
    public async Task Head_Reports_The_Size_And_Checksum_Of_A_Presigned_Put()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var key = StagingKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var head = await adapter.HeadAsync(key, CancellationToken.None);

        head.ShouldNotBeNull();
        head!.ContentLength.ShouldBe(body.LongLength);
        head.ChecksumSha256Hex.ShouldBe(Sha256Hex(body));
    }

    [Fact]
    public async Task Head_Reports_No_Checksum_When_The_Store_Records_None()
    {
        // A PUT that never sends x-amz-checksum-sha256: the store holds the bytes but records no
        // SHA-256, so HEAD must report null — "not verified", never a wrong digest (T86).
        var body = RandomNumberGenerator.GetBytes(1024);
        var key = StagingKey();
        using var adapter = Adapter();

        using var response = await PutWithoutChecksumAsync(key, body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"an unsigned-checksum PUT must be accepted; status={(int)response.StatusCode}");

        var head = await adapter.HeadAsync(key, CancellationToken.None);

        head.ShouldNotBeNull();
        head!.ContentLength.ShouldBe(body.LongLength);
        head.ChecksumSha256Hex.ShouldBeNull();
    }

    [Fact]
    public async Task Copy_From_Staging_To_Final_Preserves_Size_And_Checksum()
    {
        var body = RandomNumberGenerator.GetBytes(2048);
        var staging = StagingKey();
        var final = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(staging, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        var stagingHead = await adapter.HeadAsync(staging, CancellationToken.None);
        stagingHead.ShouldNotBeNull();
        stagingHead!.ChecksumSha256Hex.ShouldBe(Sha256Hex(body));

        await adapter.CopyAsync(staging, final, CancellationToken.None);

        var finalHead = await adapter.HeadAsync(final, CancellationToken.None);
        finalHead.ShouldNotBeNull();
        finalHead!.ContentLength.ShouldBe(stagingHead.ContentLength);
        finalHead.ChecksumSha256Hex.ShouldBe(stagingHead.ChecksumSha256Hex);
    }

    [Fact]
    public async Task Copy_Is_Idempotent_When_Run_Twice()
    {
        var body = RandomNumberGenerator.GetBytes(1024);
        var staging = StagingKey();
        var final = FinalKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(staging, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");

        await adapter.CopyAsync(staging, final, CancellationToken.None);
        var firstHead = await adapter.HeadAsync(final, CancellationToken.None);

        // Overwriting an existing destination must succeed with the same result.
        await adapter.CopyAsync(staging, final, CancellationToken.None);
        var secondHead = await adapter.HeadAsync(final, CancellationToken.None);

        secondHead.ShouldNotBeNull();
        secondHead!.ContentLength.ShouldBe(firstHead!.ContentLength);
        secondHead.ChecksumSha256Hex.ShouldBe(firstHead.ChecksumSha256Hex);
        secondHead.ChecksumSha256Hex.ShouldBe(Sha256Hex(body));
    }

    [Fact]
    public async Task Copy_Refuses_A_Pending_Destination()
    {
        using var adapter = Adapter();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            adapter.CopyAsync(StagingKey(), StagingKey(), CancellationToken.None));

        exception.ParamName.ShouldBe("destinationKey");
    }

    [Fact]
    public async Task Copy_Refuses_A_Non_Pending_Source()
    {
        using var adapter = Adapter();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            adapter.CopyAsync(FinalKey(), FinalKey(), CancellationToken.None));

        exception.ParamName.ShouldBe("sourceKey");
    }

    [Fact]
    public async Task Delete_Removes_The_Object()
    {
        var body = RandomNumberGenerator.GetBytes(512);
        var key = StagingKey();
        using var adapter = Adapter();

        using var response = await PutAsync(
            adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body))), body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");
        (await adapter.HeadAsync(key, CancellationToken.None)).ShouldNotBeNull();

        await adapter.DeleteAsync(key, CancellationToken.None);

        (await adapter.HeadAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_Of_A_Missing_Key_Does_Not_Throw()
    {
        using var adapter = Adapter();

        // The 24 h pending/ lifecycle may already have collected the staging object (T86): the
        // delete of a key that is not there must complete, not throw.
        await adapter.DeleteAsync(StagingKey(), CancellationToken.None);
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
    public async Task Head_And_Delete_Refuse_A_Key_With_Traversal_Or_An_Empty_Segment(string key)
    {
        using var adapter = Adapter();

        var head = await Should.ThrowAsync<ArgumentException>(() =>
            adapter.HeadAsync(key, CancellationToken.None));
        var delete = await Should.ThrowAsync<ArgumentException>(() =>
            adapter.DeleteAsync(key, CancellationToken.None));

        head.ParamName.ShouldBe("objectKey");
        delete.ParamName.ShouldBe("objectKey");
        AssertNoKeyInMessage(head, key);
        AssertNoKeyInMessage(delete, key);
    }

    [Fact]
    public async Task Copy_Refuses_A_Source_With_Traversal()
    {
        var key = "pending/../../etc/passwd";
        using var adapter = Adapter();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            adapter.CopyAsync(key, FinalKey(), CancellationToken.None));

        exception.ParamName.ShouldBe("sourceKey");
        AssertNoKeyInMessage(exception, key);
    }

    [Fact]
    public async Task A_Failed_Copy_Does_Not_Leak_The_Key_Or_The_Endpoint()
    {
        var missingSource = StagingKey();
        var final = FinalKey();
        using var adapter = Adapter();

        var exception = await Should.ThrowAsync<ObjectStorageException>(() =>
            adapter.CopyAsync(missingSource, final, CancellationToken.None));

        exception.Operation.ShouldBe("copy");
        exception.HttpStatus.ShouldBe(HttpStatusCode.NotFound);
        AssertNoKeyInMessage(exception, missingSource);
        AssertNoKeyInMessage(exception, final);
        exception.Message.ShouldNotContain(_fixture.Target.ServiceUrl, Case.Sensitive);
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

    private static void AssertNoKeyInMessage(Exception exception, string key)
    {
        if (key.Length > 0)
        {
            exception.Message.ShouldNotContain(key, Case.Sensitive);
        }
    }

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

    /// <summary>
    /// A signed PUT that signs only <c>Content-Length</c> — the one shape the adapter cannot produce
    /// (its presign always binds the checksum), used to store an object with no recorded SHA-256.
    /// </summary>
    private async Task<HttpResponseMessage> PutWithoutChecksumAsync(string key, byte[] body)
    {
        using var client = _fixture.Target.CreateClient();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _fixture.Target.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Protocol = Protocol.HTTP,
            Expires = DateTime.UtcNow.AddMinutes(10),
        };
        request.Headers[ContentLengthHeaderName] =
            body.LongLength.ToString(CultureInfo.InvariantCulture);

        using var message = new HttpRequestMessage(
            HttpMethod.Put, new Uri(await client.GetPreSignedURLAsync(request), UriKind.Absolute))
        {
            Content = new ByteArrayContent(body),
        };

        return await _http.SendAsync(message);
    }
}
