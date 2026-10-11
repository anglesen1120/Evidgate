// 06.1.2-T1 QA: the presigned PUT against the real SeaweedFS container, negative cases that the
// adapter suite does not cover. Expectations come from the plan (06.1.2 §2 AC3, §8) and the signed-URL
// contract (Content-Length and x-amz-checksum-sha256 are signed), not from current behaviour.
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Evidgate.BuildingBlocks.ObjectStorage;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class ObjectStorageQaTests : IDisposable
{
    private readonly SeaweedFsFixture _fixture;
    private readonly HttpClient _http = new();

    public ObjectStorageQaTests(SeaweedFsFixture fixture) => _fixture = fixture;

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Put_With_Fewer_Bytes_Than_The_Signed_Length_Is_Refused_And_Stores_Nothing()
    {
        var signedBody = RandomNumberGenerator.GetBytes(1024);
        var uploadedBody = signedBody[..512];
        var key = StagingKey();
        using var adapter = Adapter();

        // The checksum covers the uploaded bytes, so the signed Content-Length is the only violation.
        var presigned = adapter.PresignPut(new PresignPutRequest(key, signedBody.LongLength, Sha256Hex(uploadedBody)));

        using var response = await PutAsync(presigned.Url, presigned.RequiredHeaders, uploadedBody);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"a PUT of {uploadedBody.LongLength} bytes against Content-Length={signedBody.LongLength} must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(key)).ShouldBeTrue("a refused short PUT must not store anything");
    }

    [Fact]
    public async Task Put_Without_The_Signed_Checksum_Header_Is_Refused_And_Stores_Nothing()
    {
        // x-amz-checksum-sha256 is part of SignedHeaders; a client that drops it must not get a stored object.
        var body = RandomNumberGenerator.GetBytes(2048);
        var key = StagingKey();
        using var adapter = Adapter();

        var presigned = adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body)));
        var headers = presigned.RequiredHeaders
            .Where(h => !string.Equals(h.Key, "x-amz-checksum-sha256", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

        using var response = await PutAsync(presigned.Url, headers, body);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"a PUT that drops the signed checksum header must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(key)).ShouldBeTrue("a PUT without the signed checksum must not store anything");
    }

    [Fact]
    public async Task Put_With_A_Checksum_Header_Of_Other_Bytes_Is_Refused_And_Stores_Nothing()
    {
        // The header digests a different body than the one sent: the store must compare, not trust the header.
        var body = RandomNumberGenerator.GetBytes(2048);
        var otherBody = RandomNumberGenerator.GetBytes(2048);
        var key = StagingKey();
        using var adapter = Adapter();

        var presigned = adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body)));
        var headers = presigned.RequiredHeaders.ToDictionary(
            h => h.Key,
            h => string.Equals(h.Key, "x-amz-checksum-sha256", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToBase64String(SHA256.HashData(otherBody))
                : h.Value,
            StringComparer.OrdinalIgnoreCase);

        using var response = await PutAsync(presigned.Url, headers, body);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"a PUT whose checksum header digests other bytes must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(key)).ShouldBeTrue("the mismatched PUT must not store anything");
    }

    [Fact]
    public async Task Presigned_Url_With_A_Tampered_Expiry_Is_Refused_And_Stores_Nothing()
    {
        // X-Amz-Expires is part of the signed query; raising it after signing must break the signature.
        var body = RandomNumberGenerator.GetBytes(2048);
        var key = StagingKey();
        using var adapter = Adapter();

        var presigned = adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body)));
        var original = presigned.Url.OriginalString;
        // Read the X-Amz-Expires value the SDK actually signed, whatever its exact form.
        var match = System.Text.RegularExpressions.Regex.Match(original, @"X-Amz-Expires=(\d+)");
        match.Success.ShouldBeTrue("the presigned URL must carry X-Amz-Expires");
        var expires = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var tampered = new Uri(original.Replace($"X-Amz-Expires={expires}", $"X-Amz-Expires={expires + 1}", StringComparison.Ordinal));

        using var response = await PutAsync(tampered, presigned.RequiredHeaders, body);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"a URL whose signed expiry was edited must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(key)).ShouldBeTrue("a PUT on a tampered URL must not store anything");
    }

    [Fact]
    public async Task Presigned_Url_Expiry_Does_Not_Exceed_The_Configured_Cap()
    {
        // Plan 06.1.2 §8 and 00 §9.3: a presigned PUT lives at most the configured cap (900 s by default).
        var body = RandomNumberGenerator.GetBytes(2048);
        using var adapter = Adapter();

        var presigned = adapter.PresignPut(new PresignPutRequest(StagingKey(), body.LongLength, Sha256Hex(body)));
        var match = System.Text.RegularExpressions.Regex.Match(presigned.Url.OriginalString, @"X-Amz-Expires=(\d+)");
        match.Success.ShouldBeTrue("the presigned URL must carry X-Amz-Expires");
        var expires = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        (expires <= 900).ShouldBeTrue($"the signed expiry must not exceed the 900 s cap; was {expires}");
    }

    [Fact]
    public async Task Presigned_Url_Retargeted_To_Another_Staging_Key_Is_Refused()
    {
        // The signature covers the key in the path; a URL edited to point at another pending key must fail.
        var body = RandomNumberGenerator.GetBytes(2048);
        var key = StagingKey();
        var otherKey = StagingKey();
        using var adapter = Adapter();

        var presigned = adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body)));
        var original = presigned.Url.OriginalString;
        original.ShouldContain(key);
        var retargeted = new Uri(original.Replace(key, otherKey, StringComparison.Ordinal));

        using var response = await PutAsync(retargeted, presigned.RequiredHeaders, body);

        response.IsSuccessStatusCode.ShouldBeFalse(
            $"a URL retargeted to another key must be refused; status={(int)response.StatusCode}");
        (await IsAbsentAsync(otherKey)).ShouldBeTrue("the retargeted key must not receive the body");
        (await IsAbsentAsync(key)).ShouldBeTrue("the original key must not receive the body either");
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

    private static string StagingKey() => $"pending/{Guid.NewGuid():D}/{Guid.NewGuid():D}";

    private static string Sha256Hex(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>Sends the signed headers verbatim; Content-Length is left to the content, as a customer client does.</summary>
    private async Task<HttpResponseMessage> PutAsync(Uri url, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(body),
        };
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            message.Content.Headers.TryAddWithoutValidation(name, value);
        }

        return await _http.SendAsync(message);
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
}
