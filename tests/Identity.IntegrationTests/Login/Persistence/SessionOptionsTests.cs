using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Persistence;
using Evidgate.Modules.Identity.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// 02.3.1-T1.4 configuration: <c>SessionOptionsValidator</c> at its boundaries, and the rule that the pepper
/// comes from <see cref="ISecretProvider"/> — never from the configuration binder — with
/// <c>AddSessions</c> as the only place that wires it.
/// </summary>
/// <remarks>
/// No database is touched, but the class carries the Docker trait like every other class of this project:
/// the assembly fixture needs the container whether or not one test of the class reaches it.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionOptionsTests
{
    /// <summary>A pepper of exactly the accepted minimum: 32 bytes, base64.</summary>
    private static readonly string ValidPepper = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    /// <summary>An absent pepper is allowed at boot: session creation then answers 503 instead of failing the host.</summary>
    [Fact]
    public void Absent_Pepper_Is_Valid()
    {
        Validate(new SessionOptions()).ShouldBeEmpty();
        Validate(new SessionOptions { Pepper = " " }).ShouldBeEmpty();
    }

    /// <summary>A present pepper must be base64 of at least 32 bytes; 31 bytes and non-base64 are refused, naming the key only.</summary>
    [Fact]
    public void Present_Pepper_Must_Be_Base64_Of_At_Least_32_Bytes()
    {
        Validate(new SessionOptions { Pepper = ValidPepper }).ShouldBeEmpty();

        var shortPepper = Validate(new SessionOptions
        {
            Pepper = Convert.ToBase64String(new byte[31]),
        });
        shortPepper.ShouldContain(failure => failure.Contains("Pepper", StringComparison.Ordinal));
        shortPepper.ShouldContain(failure => failure.Contains("32 bytes", StringComparison.Ordinal));

        var notBase64 = Validate(new SessionOptions { Pepper = "not base64 at all!" });
        notBase64.ShouldContain(failure => failure.Contains("not valid base64", StringComparison.Ordinal));

        notBase64.ShouldNotContain(
            failure => failure.Contains("not base64 at all", StringComparison.Ordinal),
            "a message names the key, never a value");
    }

    /// <summary>The pepper version is stored beside every hash, so it starts at 1.</summary>
    [Fact]
    public void Pepper_Version_Starts_At_One()
    {
        Validate(new SessionOptions { PepperVersion = 0 })
            .ShouldContain(failure => failure.Contains("PepperVersion", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two lifetimes: the idle one may not be shorter than a minute or outlive the absolute one, and no
    /// absolute lifetime exceeds a year.
    /// </summary>
    [Fact]
    public void Lifetimes_Are_Bounded_And_Ordered()
    {
        Validate(new SessionOptions { IdleSeconds = 60, AbsoluteSeconds = 60 }).ShouldBeEmpty();
        Validate(new SessionOptions()).ShouldBeEmpty();

        Validate(new SessionOptions { IdleSeconds = 59, AbsoluteSeconds = 3600 })
            .ShouldContain(failure => failure.Contains("IdleSeconds", StringComparison.Ordinal));

        Validate(new SessionOptions { IdleSeconds = 7200, AbsoluteSeconds = 3600 })
            .ShouldContain(failure => failure.Contains("must not exceed AbsoluteSeconds", StringComparison.Ordinal));

        Validate(new SessionOptions { IdleSeconds = 3600, AbsoluteSeconds = 31_536_001 })
            .ShouldContain(failure => failure.Contains("AbsoluteSeconds", StringComparison.Ordinal));

        // Boundary: exactly one year is accepted.
        Validate(new SessionOptions { IdleSeconds = 60, AbsoluteSeconds = 31_536_000 }).ShouldBeEmpty();
    }

    /// <summary>
    /// The pepper is a secret (00 §5.7 S3): it reaches the options through <see cref="ISecretProvider"/> and
    /// an equal-looking literal in configuration never becomes the value — including the case where the
    /// provider has nothing, which must leave the option empty rather than fall back to the binder.
    /// </summary>
    [Fact]
    public void Pepper_Comes_From_The_Secret_Provider_Not_From_Binding()
    {
        var fromProvider = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        var literal = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

        Resolve(literal, fromProvider).Pepper.ShouldBe(fromProvider);
        Resolve(literal, fromProvider).Pepper.ShouldNotBe(literal);

        Resolve(literal, providerValue: null).Pepper.ShouldBeNull(
            "a literal in configuration must never become the session pepper");
    }

    /// <summary>
    /// <c>AddSessions</c> is the composition a host gets: the options, the pepper's post-configurer, the
    /// identity store and the session store — with <c>TryAdd</c>, so a host that replaces a seam keeps its own.
    /// </summary>
    [Fact]
    public void AddSessions_Registers_Both_Persistence_Seams_Once()
    {
        var services = new ServiceCollection();
        services.AddSessions(Configuration(ValidPepper));

        services.Where(descriptor => descriptor.ServiceType == typeof(ISessions)).Count().ShouldBe(1);
        services.Single(descriptor => descriptor.ServiceType == typeof(ISessions))
            .ImplementationType.ShouldBe(typeof(SessionStore));
        services.Where(descriptor => descriptor.ServiceType == typeof(ILoginIdentityStore)).Count().ShouldBe(1);
        services.Single(descriptor => descriptor.ServiceType == typeof(ILoginIdentityStore))
            .ImplementationType.ShouldBe(typeof(NpgsqlLoginIdentityStore));

        // A host that already registered its own seam keeps it (TryAdd, not Add).
        var replaced = new ServiceCollection();
        replaced.AddSingleton<ISessions>(new StubSessions());
        replaced.AddSessions(Configuration(ValidPepper));
        replaced.Where(descriptor => descriptor.ServiceType == typeof(ISessions)).Count().ShouldBe(1);
        using (var replacedProvider = replaced.BuildServiceProvider())
        {
            replacedProvider.GetRequiredService<ISessions>().ShouldBeOfType<StubSessions>();
        }

        // The pepper resolves through the provider, so the hash material is live.
        using var provider = Build(Configuration(ValidPepper), ValidPepper);
        provider.GetRequiredService<SessionSecrets>().CanHash.ShouldBeTrue();
    }

    /// <summary>The validator's failures for one options snapshot.</summary>
    private static string[] Validate(SessionOptions options) =>
        new SessionOptionsValidator()
            .Validate(Options.DefaultName, options)
            .Failures
            ?.ToArray() ?? [];

    /// <summary>The options a host would materialise for one configuration and one provider value.</summary>
    private static SessionOptions Resolve(string pepperLiteral, string? providerValue)
    {
        using var provider = Build(Configuration(pepperLiteral), providerValue);

        return provider.GetRequiredService<IOptions<SessionOptions>>().Value;
    }

    /// <summary>The session group's configuration with the given pepper literal; the provider answers with its own value.</summary>
    private static IConfiguration Configuration(string pepperLiteral) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:SESSION:PEPPER"] = pepperLiteral,
                ["EVIDGATE:SESSION:PEPPERVERSION"] = "1",
                ["EVIDGATE:SESSION:IDLESECONDS"] = "28800",
                ["EVIDGATE:SESSION:ABSOLUTESECONDS"] = "604800",
            })
            .Build();

    /// <summary>A provider over <paramref name="configuration"/> whose secret seam answers <paramref name="providerValue"/>.</summary>
    private static ServiceProvider Build(IConfiguration configuration, string? providerValue)
    {
        var services = new ServiceCollection();
        services.AddSessions(configuration);
        services.AddSingleton<ISecretProvider>(new FixedSecretProvider(providerValue));

        return services.BuildServiceProvider();
    }

    /// <summary>A session seam a host registered itself; <c>AddSessions</c> must not replace it.</summary>
    private sealed class StubSessions : ISessions
    {
        public ValueTask<Result<SessionTicket>> CreateAsync(
            SessionCreation request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Failure<SessionTicket>(new Error("stub", "stub", 500)));
    }

    /// <summary>The platform's secret seam with one value (only the name the session group asks for is answered).</summary>
    private sealed class FixedSecretProvider(string? value) : ISecretProvider
    {
        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(string.Equals(name, SecretNames.SessionPepper, StringComparison.Ordinal) ? value : null);
    }
}
