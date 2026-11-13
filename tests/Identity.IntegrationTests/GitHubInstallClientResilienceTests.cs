using System.Diagnostics;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The two installation clients answer <c>github_unavailable</c> when their own resilience pipeline gives up: a
/// rejected execution (the total-budget timeout, a circuit break) is an outage, never an exception that escapes a
/// route. The handler throws what the pipeline throws, so no socket is opened and no GitHub call is made.
/// </summary>
public sealed class GitHubInstallClientResilienceTests
{
    [Fact]
    public async Task OAuthExchange_PipelineRejection_IsUnavailable_NotAnException()
    {
        var client = new GitHubOAuthClient(
            new RejectingHttpClientFactory(),
            Options.Create(new GitHubOptions
            {
                AppId = "1",
                ClientId = "Iv1.probe",
                ClientSecret = "probe-secret",
            }),
            TimeProvider.System,
            NullLogger<GitHubOAuthClient>.Instance);

        var result = await client.ExchangeCodeAsync("probe-code", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
    }

    /// <summary>The token lookup goes through the same catch as the OAuth exchange; a refused JWT short-circuits it.</summary>
    [Fact]
    public async Task InstallationLookup_WithoutConfiguredApp_IsNotConfigured_NotAnException()
    {
        var client = new GitHubAppInstallationsClient(
            new RejectingHttpClientFactory(),
            new GitHubAppTokenSource(
                Options.Create(new GitHubOptions { AppId = "1" }),
                TimeProvider.System,
                NullLogger<GitHubAppTokenSource>.Instance),
            Options.Create(new GitHubOptions { AppId = "1" }),
            TimeProvider.System,
            NullLogger<GitHubAppInstallationsClient>.Instance);

        var result = await client.GetAsync(1, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
    }

    /// <summary>An <see cref="IHttpClientFactory"/> whose client throws what Polly throws when it rejects an attempt.</summary>
    private sealed class RejectingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RejectingHandler(), disposeHandler: false);
    }

    /// <summary>Answers like a pipeline that spent its budget: a rejection, without a request leaving the process.</summary>
    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new TimeoutRejectedException("the pipeline rejected the attempt", new TimeSpan(0, 0, 1));
    }
}
