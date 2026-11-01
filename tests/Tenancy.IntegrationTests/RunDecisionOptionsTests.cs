using System.Globalization;
using Evidgate.Modules.Policy.Application.Queries;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The run-page link without a host: the URL is built from the configured origin alone, so a base path cannot swallow a
/// project segment, and anything that is not an absolute https origin without credentials serves no link at all.
/// </summary>
public sealed class RunDecisionOptionsTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static readonly Guid Run = Guid.Parse("22222222-2222-7222-8222-222222222222");

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("app.evidgate.example", null)]
    [InlineData("/projects/11111111-1111-7111-8111-111111111111", null)]
    [InlineData("http://app.evidgate.example", null)]
    [InlineData("https://", null)]
    [InlineData("https://user@app.evidgate.example", null)]
    [InlineData("https://user" + ":" + "pw" + "@app.evidgate.example", null)]
    [InlineData("https://app.evidgate.example", "https://app.evidgate.example")]
    [InlineData("https://app.evidgate.example/", "https://app.evidgate.example")]
    [InlineData("https://app.evidgate.example:8443", "https://app.evidgate.example:8443")]
    [InlineData("https://app.evidgate.example/base/path", "https://app.evidgate.example")]
    public void RunUrl_IsBuiltFromTheConfiguredOrigin(string? publicBaseUrl, string? expectedOrigin)
    {
        var expected = expectedOrigin is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{expectedOrigin}/projects/{Project:D}/runs/{Run:D}");

        new RunDecisionOptions { PublicBaseUrl = publicBaseUrl }.RunUrl(Project, Run).ShouldBe(expected);
    }
}
