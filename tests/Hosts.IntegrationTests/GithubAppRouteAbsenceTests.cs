using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The platform is not a GitHub App: it serves no installation, repository-binding or inbound GitHub webhook
/// route. GitHub sign-in stays, but it rides the generic <c>/v1/auth/oidc/{provider}</c> routes, so no template
/// names GitHub. The scan reads the real host's endpoint table, so a route mapped without a contract entry counts.
/// </summary>
public sealed class GithubAppRouteAbsenceTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    [Fact]
    public async Task NoGithubInstallationBindingOrWebhookRoute()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);

        var templates = ServedTemplates(factory.Services);

        // Positive controls: the scan sees the surviving login and outbound-webhook routes, so an empty or partial
        // table cannot make the absence below pass.
        templates.ShouldContain("/v1/auth/oidc/{provider}/callback");
        templates.ShouldContain("/v1/projects/{p}/webhook-endpoints");
        templates.Count.ShouldBeGreaterThan(40, "a partial route table cannot prove an absence");

        var retired = templates.Where(IsRetiredGithubRoute).ToList();
        retired.ShouldBeEmpty("the GitHub App is removed; a route for it was found: " + string.Join(", ", retired));
    }

    [Theory]
    [InlineData("/v1/github/installations", true)]
    [InlineData("/v1/github/installations/start", true)]
    [InlineData("/v1/github/installations/callback", true)]
    [InlineData("/V1/GitHub/Installations/{i}", true)]
    [InlineData("/v1/projects/{p}/git-repo-bindings", true)]
    [InlineData("/v1/projects/{p}/Git-Repo-Bindings/{b}", true)]
    [InlineData("/v1/webhooks/github", true)]
    [InlineData("/webhooks/github/", true)]
    [InlineData("/v1/auth/oidc/{provider}/start", false)]
    [InlineData("/v1/auth/oidc/{provider}/callback", false)]
    [InlineData("/v1/projects/{p}/webhook-endpoints", false)]
    [InlineData("/v1/projects/{p}/webhook-endpoints/{e}", false)]
    public void IsRetiredGithubRoute_ClassifiesTemplate(string template, bool expected)
    {
        IsRetiredGithubRoute(template).ShouldBe(expected);
    }

    private static bool IsRetiredGithubRoute(string template) =>
        template.Contains("github/installations", StringComparison.OrdinalIgnoreCase)
        || template.Contains("git-repo-bindings", StringComparison.OrdinalIgnoreCase)
        || template.Contains("webhooks/github", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every route template the real host serves (all prefixes, so a route outside <c>/v1</c> counts).</summary>
    private static List<string> ServedTemplates(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(route => string.IsNullOrWhiteSpace(route.RoutePattern.RawText)
                ? route.RoutePattern.ToString() ?? string.Empty
                : route.RoutePattern.RawText)
            .Select(template => template.StartsWith('/') ? template : "/" + template)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
