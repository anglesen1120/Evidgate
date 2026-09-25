using System.Net;
using System.Security.Cryptography;
using Shouldly;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// The 01.8.1-T1 (T18) presigned-URL assertion set. It is endpoint-agnostic on
/// purpose: the local suite hands it a SeaweedFS container target and 10.6.1-T2
/// runs the same three assertions against the real provider, so "local green"
/// means the same thing as "provider green".
/// </summary>
internal sealed class S3ParityAssertions(S3ParityTarget target, HttpClient http)
{
    /// <summary>06.1.2 §5: the presigned URL expires within 15 minutes; tests use 10.</summary>
    public static readonly TimeSpan UrlExpiry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Presigned PUT (signing <c>Content-Length</c> + the SHA-256 checksum header)
    /// then presigned GET of the same key returns the exact bytes.
    /// </summary>
    public async Task<PresignedPutGetObservation> PresignedPutGet_RoundTrip(string key, byte[] body)
    {
        var checksum = Sha256Base64(body);
        var presigned = target.PresignPut(key, body.LongLength, checksum, UrlExpiry);

        // 06.1.2 AC1 / T18: the URL must bind the declared length and the checksum,
        // and must expose both as headers the holder sends back.
        presigned.Binds("Content-Length").ShouldBeTrue(
            $"the presigned PUT must sign Content-Length; X-Amz-SignedHeaders={Join(presigned.SignedHeaders)} query={Join(presigned.Query.Keys)}");
        presigned.Binds(S3ParityTarget.ChecksumHeaderName).ShouldBeTrue(
            $"the presigned PUT must sign {S3ParityTarget.ChecksumHeaderName}; X-Amz-SignedHeaders={Join(presigned.SignedHeaders)} query={Join(presigned.Query.Keys)}");
        presigned.RequiredHeaders.ShouldContainKey(
            "Content-Length",
            $"the signed Content-Length value must come back as a required header; X-Amz-SignedHeaders={Join(presigned.SignedHeaders)}");
        presigned.RequiredHeaders.ShouldContainKey(
            S3ParityTarget.ChecksumHeaderName,
            $"the signed checksum value must come back as a required header; X-Amz-SignedHeaders={Join(presigned.SignedHeaders)}");

        using var putResponse = await PutAsync(presigned, body);
        var putStatus = (int)putResponse.StatusCode;
        var putErrorCode = await ReadS3ErrorCodeAsync(putResponse);
        putResponse.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted; status={putStatus} error={putErrorCode}");

        var getUrl = target.PresignGet(key, UrlExpiry);
        using var getResponse = await http.GetAsync(getUrl.Url);
        var getStatus = (int)getResponse.StatusCode;
        getResponse.IsSuccessStatusCode.ShouldBeTrue($"the presigned GET must succeed; status={getStatus}");
        var downloaded = await getResponse.Content.ReadAsByteArrayAsync();

        downloaded.ShouldBe(body, $"the presigned GET must return the exact uploaded bytes; status={getStatus}");

        return new PresignedPutGetObservation(putStatus, getStatus, downloaded.Length, presigned.Url, presigned.SignedHeaders);
    }

    /// <summary>
    /// A PUT whose <c>Content-Length</c> exceeds the signed value must be refused
    /// by the store and must leave nothing behind (AC2 / T18). The checksum sent
    /// is the correct one for the body, so the signed length is the only
    /// violation.
    /// </summary>
    public async Task<PresignedRejectionObservation> OversizeContentLength_Rejected(
        string key,
        byte[] body,
        long signedContentLength)
    {
        body.LongLength.ShouldBeGreaterThan(
            signedContentLength,
            "the oversize case must upload more bytes than the URL signed");
        var checksum = Sha256Base64(body);
        var presigned = target.PresignPut(key, signedContentLength, checksum, UrlExpiry);

        using var putResponse = await PutAsync(presigned, body);
        var putStatus = (int)putResponse.StatusCode;
        var putErrorCode = await ReadS3ErrorCodeAsync(putResponse);
        putResponse.IsSuccessStatusCode.ShouldBeFalse(
            $"a PUT carrying {body.LongLength} bytes against a URL signing Content-Length={signedContentLength} must be refused; status={putStatus}");

        var notStored = await IsAbsentAsync(key);
        notStored.ShouldBeTrue(
            $"the refused oversize PUT must not store anything; status={putStatus} error={putErrorCode}");

        return new PresignedRejectionObservation(putStatus, putErrorCode, notStored);
    }

    /// <summary>
    /// A PUT whose body does not match the signed checksum must be refused by the
    /// store (AC2 / T18); the declared length matches, so the checksum is the only
    /// violation.
    /// </summary>
    public async Task<PresignedRejectionObservation> ChecksumHeader_Enforced(
        string key,
        byte[] signedBody,
        byte[] uploadedBody)
    {
        signedBody.ShouldNotBe(
            uploadedBody,
            "the checksum case must upload different bytes than the signed checksum covers");
        var presigned = target.PresignPut(
            key,
            uploadedBody.LongLength,
            Sha256Base64(signedBody),
            UrlExpiry);

        using var putResponse = await PutAsync(presigned, uploadedBody);
        var putStatus = (int)putResponse.StatusCode;
        var putErrorCode = await ReadS3ErrorCodeAsync(putResponse);
        putResponse.IsSuccessStatusCode.ShouldBeFalse(
            $"bytes that do not match the signed {S3ParityTarget.ChecksumHeaderName} must be refused; status={putStatus}");
        putErrorCode.ShouldNotBeNullOrWhiteSpace(
            $"the refusal must carry an S3 error code; status={putStatus}");

        var notStored = await IsAbsentAsync(key);
        notStored.ShouldBeTrue(
            $"the refused mismatched PUT must not store anything; status={putStatus} error={putErrorCode}");

        return new PresignedRejectionObservation(putStatus, putErrorCode, notStored);
    }

    /// <summary>
    /// Sends the request a customer client sends: a raw <see cref="HttpClient"/>
    /// with the signed headers verbatim. <c>Content-Length</c> is left to the
    /// content, so a body that differs in size from the signed length produces a
    /// different header value on the wire — exactly what T18 says must fail.
    /// </summary>
    private async Task<HttpResponseMessage> PutAsync(PresignedRequest presigned, byte[] body)
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

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await http.SendAsync(message);
    }

    /// <summary>A rejected upload must not be readable afterwards (nothing stored).</summary>
    private async Task<bool> IsAbsentAsync(string key)
    {
        var getUrl = target.PresignGet(key, UrlExpiry);
        using var response = await http.GetAsync(getUrl.Url);
        return response.StatusCode == HttpStatusCode.NotFound;
    }

    private static string Sha256Base64(byte[] body) =>
        Convert.ToBase64String(SHA256.HashData(body));

    /// <summary>Reads the <c>&lt;Code&gt;</c> member of an S3 error document, for the report.</summary>
    private static async Task<string> ReadS3ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        const string open = "<Code>";
        const string close = "</Code>";
        var start = body.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = body.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        return end < 0 ? string.Empty : body[(start + open.Length)..end];
    }

    private static string Join(IEnumerable<string> values) => string.Join(",", values);
}

/// <summary>Observed values of the round-trip parity assertion (evidence, not a verdict).</summary>
internal sealed record PresignedPutGetObservation(
    int PutStatus,
    int GetStatus,
    int DownloadedBytes,
    Uri PutUrl,
    IReadOnlyList<string> SignedHeaders);

/// <summary>Observed values of a rejection parity assertion (evidence, not a verdict).</summary>
internal sealed record PresignedRejectionObservation(int PutStatus, string PutErrorCode, bool ObjectAbsent);
