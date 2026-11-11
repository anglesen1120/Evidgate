using Evidgate.BuildingBlocks.Persistence.Isolation;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// Two Api instances over one database share the Data Protection ring and the login replay set: a cookie
/// payload protected by one is read by the other, a replayed login <c>state</c> is refused by the other, a
/// restarted instance keeps the ring, and a host whose key store is unreachable refuses to start instead of
/// running on an ephemeral ring.
/// </summary>
[Trait("Category", "Docker")]
public sealed class SharedKeyRingTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly PostgresFixture _postgres;

    public SharedKeyRingTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_Cookie_Payload_Protected_By_Instance_A_Is_Read_By_Instance_B()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        await using var a = new EvidgateApiFactory(database);
        await using var b = new EvidgateApiFactory(database);

        var protectedByA = a.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose).Protect("pending-login");

        b.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose).Unprotect(protectedByA)
            .ShouldBe("pending-login");
    }

    [Fact]
    public async Task A_Login_State_Consumed_On_Instance_A_Is_Refused_On_Instance_B()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        await using var a = new EvidgateApiFactory(database);
        await using var b = new EvidgateApiFactory(database);
        var ct = TestContext.Current.CancellationToken;

        (await a.Services.GetRequiredService<ILoginReplayCache>().TryConsumeAsync("github:state-1", Lifetime, ct)).ShouldBeTrue();
        (await b.Services.GetRequiredService<ILoginReplayCache>().TryConsumeAsync("github:state-1", Lifetime, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Restarted_Instance_Reads_Cookies_Protected_Before_The_Restart()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        string protectedBefore;

        await using (var first = new EvidgateApiFactory(database))
        {
            protectedBefore = first.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(LoginStateCookie.ProtectorPurpose).Protect("pending-login");
        }

        await using var second = new EvidgateApiFactory(database);
        second.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose).Unprotect(protectedBefore)
            .ShouldBe("pending-login");
    }

    [Fact]
    public async Task An_Unreachable_Key_Store_Stops_The_Start_And_Never_Yields_An_Ephemeral_Ring()
    {
        await using var closed = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=evidgate_app;Timeout=1;Pooling=false");
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton(new DataProtectionKeyRepository(closed));
        services.AddOptions<KeyManagementOptions>()
            .Configure<DataProtectionKeyRepository>((keys, repository) => keys.XmlRepository = repository);
        await using var provider = services.BuildServiceProvider();

        var check = new DataProtectionKeyRingStartupCheck(
            provider.GetRequiredService<IKeyManager>(),
            Options.Create(new IsolationSelfCheckOptions()));

        await Should.ThrowAsync<Exception>(() => check.StartAsync(TestContext.Current.CancellationToken));
        Should.Throw<Exception>(() => provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("x"));
    }

    [Fact]
    public async Task The_Startup_Check_Creates_The_First_Key_And_A_Second_Check_Reuses_It()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(database);

        IKeyManager Manager()
        {
            var services = new ServiceCollection();
            services.AddDataProtection();
            services.AddSingleton(new DataProtectionKeyRepository(source));
            services.AddOptions<KeyManagementOptions>()
                .Configure<DataProtectionKeyRepository>((keys, repository) => keys.XmlRepository = repository);

            return services.BuildServiceProvider().GetRequiredService<IKeyManager>();
        }

        var options = Options.Create(new IsolationSelfCheckOptions());
        await new DataProtectionKeyRingStartupCheck(Manager(), options).StartAsync(TestContext.Current.CancellationToken);
        await new DataProtectionKeyRingStartupCheck(Manager(), options).StartAsync(TestContext.Current.CancellationToken);

        Manager().GetAllKeys().Count.ShouldBe(1);
    }

    /// <summary>
    /// The same refusal through the composed host, with the isolation self-check at its production
    /// <c>Required</c> default: the host starts, its real registration is pointed at a closed port, and the
    /// start is what fails — not the first login.
    /// </summary>
    [Fact]
    public async Task The_Composed_Host_Refuses_To_Start_When_Its_Key_Store_Is_Unreachable()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        await using var closed = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=evidgate_app;Timeout=1;Pooling=false");
        using var factory = new RequiredSelfCheckApiFactory(database, services =>
        {
            // The repository is the only ring source, so replacing the registration replaces the source itself.
            services.RemoveAll<DataProtectionKeyRepository>();
            services.AddSingleton(new DataProtectionKeyRepository(closed));
        });

        Should.Throw<Exception>(() => factory.StartServer());
    }

    /// <summary>The control: the same composition with the real repository and a passing isolation check boots and holds a ring.</summary>
    [Fact]
    public async Task The_Composed_Host_Boots_When_Isolation_And_The_Key_Store_Pass()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        using var factory = new RequiredSelfCheckApiFactory(database);

        factory.StartServer();

        factory.Services.GetRequiredService<IKeyManager>().GetAllKeys().ShouldNotBeEmpty();
    }
}

/// <summary>
/// The Api composition with the production boot policy: the isolation self-check stays at its <c>Required</c>
/// default, unlike <see cref="EvidgateApiFactory"/>, which disables it for hosts that deliberately boot without
/// a database.
/// </summary>
internal sealed class RequiredSelfCheckApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureTestServices = null) : WebApplicationFactory<Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = connectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
            }));

        builder.ConfigureTestServices(services => configureTestServices?.Invoke(services));
    }
}
