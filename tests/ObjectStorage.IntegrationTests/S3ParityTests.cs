using System.Text;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// 01.8.1-T1 (AC2, plan §9 "Presigned parity", T18): the local S3 emulator must
/// honour the same presigned-URL contract the provider does, so a local green
/// run means something. Every assertion is executed by
/// <see cref="S3ParityAssertions"/>, which 10.6.1-T2 reuses against the real
/// provider — a failure here is a parity gap, never a test to relax.
/// </summary>
[Trait("Category", "Docker")]
[Collection(SeaweedFsCollectionDefinition.Name)]
public sealed class S3ParityTests(SeaweedFsFixture seaweedFs, ITestOutputHelper output)
{
    private static readonly byte[] RoundTripBody =
        Encoding.UTF8.GetBytes("evidgate-s3-parity-roundtrip-payload\n");

    private static readonly byte[] OversizeBody =
        [.. RoundTripBody, .. Encoding.UTF8.GetBytes("0123456789abcdef")];

    private static readonly byte[] SignedChecksumBody =
        Encoding.UTF8.GetBytes("evidgate-s3-parity-checksum-A\n");

    private static readonly byte[] DifferingChecksumBody =
        Encoding.UTF8.GetBytes("evidgate-s3-parity-checksum-B\n");

    /// <summary>AC2/T18: presigned PUT (signed <c>Content-Length</c> + checksum header) then GET returns the bytes.</summary>
    [Fact]
    public async Task S3_Parity_PresignedPutGet_RoundTrip()
    {
        var key = PendingKey("roundtrip");
        using var http = new HttpClient();
        var observed = await Assertions(http).PresignedPutGet_RoundTrip(key, RoundTripBody);

        output.WriteLine(
            $"key={key} endpoint={observed.PutUrl.GetLeftPart(UriPartial.Authority)} put={observed.PutStatus} get={observed.GetStatus} bytes={observed.DownloadedBytes} signedHeaders={string.Join(";", observed.SignedHeaders)}");
    }

    /// <summary>AC2/T18: uploading more bytes than the URL signed must be refused by the store.</summary>
    [Fact]
    public async Task S3_Parity_OversizeContentLength_Rejected()
    {
        var key = PendingKey("oversize");
        using var http = new HttpClient();
        var observed = await Assertions(http)
            .OversizeContentLength_Rejected(key, OversizeBody, RoundTripBody.LongLength);

        output.WriteLine(
            $"key={key} signedContentLength={RoundTripBody.Length} uploadedBytes={OversizeBody.Length} put={observed.PutStatus} error={observed.PutErrorCode} objectAbsent={observed.ObjectAbsent}");
    }

    /// <summary>AC2/T18: a body that does not match the signed SHA-256 checksum must be refused by the store.</summary>
    [Fact]
    public async Task S3_Parity_ChecksumHeader_Enforced()
    {
        var key = PendingKey("checksum");
        using var http = new HttpClient();
        var observed = await Assertions(http).ChecksumHeader_Enforced(key, SignedChecksumBody, DifferingChecksumBody);

        output.WriteLine(
            $"key={key} signedBytes={SignedChecksumBody.Length} uploadedBytes={DifferingChecksumBody.Length} put={observed.PutStatus} error={observed.PutErrorCode} objectAbsent={observed.ObjectAbsent}");
    }

    private S3ParityAssertions Assertions(HttpClient http) => new(seaweedFs.Target, http);

    private static string PendingKey(string suffix) =>
        $"{S3ParityTarget.PendingPrefix}{Guid.NewGuid():N}/{suffix}";
}
