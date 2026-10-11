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
/// The real <see cref="S3ObjectStorage"/> adapter against the SeaweedFS fixture
/// (06.1.2-T1): the presigned PUT binds the declared length and the checksum, so
/// the store refuses an oversize or mismatched body, and the happy path stores the
/// bytes verbatim. The 01.8.1-T1 parity suite stays the provider-parity proof.
/// </summary>
[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class ObjectStorageAdapterTests : IDisposable
{
    private readonly SeaweedFsFixture _fixture;
    private readonly HttpClient _http = new();

    public ObjectStorageAdapterTests(SeaweedFsFixture fixture) => _fixture = fixture;

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Presigned_Put_Stores_The_Declared_Bytes_Verbatim()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var key = StagingKey();
        using var adapter = Adapter();

        var presigned = adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body)));

        using var response = await PutAsync(presigned, body);

        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={(int)response.StatusCode}");
        (await ReadStoredAsync(key)).ShouldBe(body);
    }

    [Fact]
    public async Task Put_With_More_Bytes_Than_The_Signed_Length_Is_Refused()
    {
        var signedBody = RandomNumberGenerator.GetBytes(1024);
        var uploadedBody = RandomNumberGenerator.GetBytes(2048);
        var key = StagingKey();
        using var adapter = Adapter();

        // The checksum covers the uploaded bytes, so the signed length is the only violation.
        var presigned = adapter.PresignPut(
            new PresignPutRequest(key, signedBody.LongLength, Sha256Hex(uploadedBody)));

        using var response = await PutAsync(presigned, uploadedBody);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"a PUT carrying {uploadedBody.LongLength} bytes against a URL signing Content-Length={signedBody.LongLength} must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(key)).ShouldBeTrue("the refused oversize PUT must not store anything");
    }

    [Fact]
    public async Task Put_With_Bytes_That_Do_Not_Match_The_Signed_Checksum_Is_Refused()
    {
        var signedBody = RandomNumberGenerator.GetBytes(1024);
        var uploadedBody = (byte[])signedBody.Clone();
        uploadedBody[0] ^= 0xFF;
        var key = StagingKey();
        using var adapter = Adapter();

        // The declared length matches, so the checksum is the only violation.
        var presigned = adapter.PresignPut(
            new PresignPutRequest(key, uploadedBody.LongLength, Sha256Hex(signedBody)));

        using var response = await PutAsync(presigned, uploadedBody);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"bytes that do not match the signed {S3ObjectStorage.ChecksumHeaderName} must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(key)).ShouldBeTrue("the refused mismatched PUT must not store anything");
    }

    [Theory]
    [InlineData("artifacts/1/2")]
    [InlineData("pending/../../etc/passwd")]
    [InlineData("pending//x")]
    [InlineData("pending\\x")]
    [InlineData("pending/a\nb")]
    [InlineData("")]
    public void PresignPut_Rejects_A_Key_Outside_The_Pending_Prefix(string key)
    {
        using var adapter = Adapter();

        var exception = Should.Throw<ArgumentException>(() =>
            adapter.PresignPut(new PresignPutRequest(key, 1, new string('a', 64))));

        exception.ParamName.ShouldBe("objectKey");
        if (key.Length > 0)
        {
            exception.Message.ShouldNotContain(key, Case.Sensitive);
        }
    }

    [Theory]
    [InlineData(900)]
    [InlineData(60)]
    public void Presigned_Url_Expires_Within_The_Configured_Lifetime(int expirySeconds)
    {
        using var adapter = Adapter(expirySeconds);
        var before = DateTimeOffset.UtcNow;

        var presigned = adapter.PresignPut(new PresignPutRequest(StagingKey(), 1, new string('a', 64)));
        var after = DateTimeOffset.UtcNow;

        presigned.ExpiresAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(expirySeconds));
        presigned.ExpiresAt.ShouldBeLessThanOrEqualTo(after.AddSeconds(expirySeconds));
        SignedExpirySeconds(presigned.Url).ShouldBe(expirySeconds);
    }

    private S3ObjectStorage Adapter(int expirySeconds = 900) => new(Options.Create(new ObjectStorageOptions
    {
        Endpoint = _fixture.Target.ServiceUrl,
        Region = _fixture.Target.Region,
        Bucket = _fixture.Target.Bucket,
        ForcePathStyle = _fixture.Target.ForcePathStyle,
        PresignExpirySeconds = expirySeconds,
        AccessKey = _fixture.Target.AccessKey,
        SecretKey = _fixture.Target.SecretKey,
    }));

    private static string StagingKey() => $"pending/{Guid.NewGuid():D}/{Guid.NewGuid():D}";

    private static string Sha256Hex(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>
    /// Sends the request a customer client sends: the signed headers verbatim, with
    /// <c>Content-Length</c> left to the content so a body of a different size
    /// produces a different header value on the wire.
    /// </summary>
    private async Task<HttpResponseMessage> PutAsync(PresignedPut presigned, byte[] body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, presigned.Url)
        {
            Content = new ByteArrayContent(body),
        };
        foreach (var (name, value) in presigned.RequiredHeaders)
        {
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            message.Content.Headers.TryAddWithoutValidation(name, value);
        }

        return await _http.SendAsync(message);
    }

    private async Task<byte[]> ReadStoredAsync(string key)
    {
        using var client = _fixture.Target.CreateClient();
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _fixture.Target.Bucket,
            Key = key,
        });
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);

        return buffer.ToArray();
    }

    private async Task<bool> IsAbsentAsync(string key)
    {
        using var client = _fixture.Target.CreateClient();
        try
        {
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _fixture.Target.Bucket,
                Key = key,
            });

            return false;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }

    private static int SignedExpirySeconds(Uri url)
    {
        var value = url.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .First(parts => parts[0] == "X-Amz-Expires")[1];

        return int.Parse(Uri.UnescapeDataString(value), CultureInfo.InvariantCulture);
    }
}
