using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The catalog's GitHub client is composed by the real Api host, not by a test: the repository verification of a
/// repository binding runs there, so the handler that needs it and the ownership proof it depends on must both resolve
/// from the host's own provider.
/// </summary>
public sealed class CatalogGitHubWiringTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Api_Host_ResolvesTheBindingFlow_ItsGitHubClient_AndTheOwnershipProof()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // The typed client over the shared App seam: the added composition in the Api, not a registration a test made.
        services.GetRequiredService<GitHubClient>().ShouldNotBeNull();
        // The per-request flow that calls GitHub between its two units, and the store the second unit writes through.
        services.GetRequiredService<GitRepoBindingRegistration>().ShouldNotBeNull();
        services.GetRequiredService<IGitRepoBindingStore>().ShouldNotBeNull();
        // The Identity module's ownership proof the flow refuses foreign installations with.
        services.GetRequiredService<IGitHubInstallationOwnership>().ShouldNotBeNull();
    }

    /// <summary>
    /// The Worker resolves the same flow and the same typed client — the composition its catalog sync handlers share
    /// with the Api, not a second registration a test made — so a handler moved between the two hosts keeps its GitHub
    /// pipe and the ownership proof it writes under.
    /// </summary>
    [Fact]
    public void Worker_Host_ResolvesTheBindingFlow_ItsGitHubClient_AndTheOwnershipProof()
    {
        var builder = WorkerHostFactory.Create(DeadConnectionString, HostLocator.FreePort());
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<GitHubClient>().ShouldNotBeNull();
        services.GetRequiredService<GitRepoBindingRegistration>().ShouldNotBeNull();
        services.GetRequiredService<IGitHubInstallationOwnership>().ShouldNotBeNull();
    }
}
