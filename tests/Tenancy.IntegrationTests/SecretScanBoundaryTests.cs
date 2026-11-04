using System.Diagnostics;
using System.Text;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class SecretScanBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Scan_EmptyInput_HasNoHits() => SecretPatternScanner.Scan([], Token).ShouldBeEmpty();

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    public void Scan_BearerSchemeCase_DetectsCredential(string scheme)
    {
        var bytes = Encoding.ASCII.GetBytes(scheme + " " + new string('Q', 20));
        var hit = SecretPatternScanner.Scan(bytes, Token).ShouldHaveSingleItem();
        hit.PatternClass.ShouldBe(SecretPatternClass.GenericBearerToken);
        hit.HitCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4095)]
    [InlineData(8388608)]
    public void Scan_DelimitedKeyAtBoundary_DetectsOneHit(int offset)
    {
        var key = Encoding.ASCII.GetBytes(string.Concat("AK", "IA", new string('Q', 16)));
        var bytes = new byte[offset + key.Length + 1];
        Array.Fill(bytes, (byte)' ');
        key.CopyTo(bytes, offset);
        SecretPatternScanner.Scan(bytes, Token).ShouldHaveSingleItem().HitCount.ShouldBe(1);
    }

    [Fact]
    public void Scan_BinaryWithInvalidUtf8_DetectsDelimitedKey()
    {
        var bytes = new byte[65536];
        Array.Fill(bytes, (byte)0xff);
        Encoding.ASCII.GetBytes(string.Concat("AK", "IA", new string('Q', 16))).CopyTo(bytes, 32768);
        SecretPatternScanner.Scan(bytes, Token).ShouldHaveSingleItem().HitCount.ShouldBe(1);
    }

    [Fact]
    public void Scan_RepeatedNearMatches_IsBoundedWithoutInputSizedAllocations()
    {
        var bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Bearer \t-----BEGIN X ", 500000)));
        SecretPatternScanner.Scan("warmup"u8, Token);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        SecretPatternScanner.Scan(bytes, Token).ShouldBeEmpty();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        allocated.ShouldBeLessThan(65536);
    }

    [Fact]
    public void Scan_MaximumStorageSizedBinary_CompletesAndFindsFinalKey()
    {
        var bytes = new byte[256 * 1024 * 1024];
        var key = Encoding.ASCII.GetBytes(string.Concat("AK", "IA", new string('Q', 16)));
        key.CopyTo(bytes, bytes.Length - key.Length);
        var started = Stopwatch.GetTimestamp();
        SecretPatternScanner.Scan(bytes, Token).ShouldHaveSingleItem().HitCount.ShouldBe(1);
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }
}
