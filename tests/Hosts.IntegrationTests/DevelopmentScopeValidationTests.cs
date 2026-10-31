using Evidgate.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The default Development host validates scopes and the container on build; the
/// other suites pin Production, which does not, so a singleton consuming a scoped
/// service only crashed a real Development start. These build the real composition
/// with both validations on and never start it, so no database is needed.
/// </summary>
public sealed class DevelopmentScopeValidationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    private static readonly ServiceProviderOptions Validating = new()
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    };

    [Fact]
    public void Api_Composition_Builds_With_Scope_And_Build_Validation()
    {
        using var factory = new EvidgateApiFactory(UnreachableConnectionString)
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .UseDefaultServiceProvider((_, options) =>
                {
                    options.ValidateScopes = true;
                    options.ValidateOnBuild = true;
                }));

        // Building the host is the assertion: validation throws AggregateException from Services.
        _ = factory.Services;
    }

    [Fact]
    public void Worker_Composition_Builds_With_Scope_And_Build_Validation()
    {
        var builder = WorkerHostFactory.Create(UnreachableConnectionString, healthPort: 0);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(Validating));

        using IHost host = builder.Build();
    }
}
