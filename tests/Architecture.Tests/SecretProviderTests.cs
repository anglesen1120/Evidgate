using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 10.1.1-T2: the environment-injection provider's contract — a known name resolves
/// its value, an unset/blank value resolves <see langword="null"/>, an unknown name is
/// rejected without echoing any value, and the name→configuration-key mapping is the
/// one the environment-variables provider uses.
/// </summary>
public class SecretProviderTests
{
    [Fact]
    public async Task Known_Name_Resolves_Its_Value()
    {
        var provider = new EnvironmentSecretProvider(
            name => name == SecretNames.CiPepper ? "pepper-value" : null);

        (await provider.GetSecretAsync(SecretNames.CiPepper, TestContext.Current.CancellationToken))
            .ShouldBe("pepper-value");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Unset_Or_Blank_Resolves_Null(string? value)
    {
        var provider = new EnvironmentSecretProvider(_ => value);

        (await provider.GetSecretAsync(SecretNames.DatabaseConnectionString, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_Name_Throws_Without_The_Value()
    {
        var provider = new EnvironmentSecretProvider(_ => "a-value-that-must-not-appear");

        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await provider.GetSecretAsync("NOT_A_SECRET_NAME"));

        exception.Message.ShouldContain("NOT_A_SECRET_NAME");
        exception.Message.ShouldNotContain("a-value-that-must-not-appear");
    }

    [Fact]
    public void Configuration_Key_Maps_Double_Underscore_To_Colon_Preserving_Case()
    {
        // The mapping only replaces "__" with ":" — exactly what the
        // environment-variables configuration source does — and keeps the case.
        SecretNames.ToConfigurationKey(SecretNames.CiPepper).ShouldBe("EVIDGATE:CI:PEPPER");
        SecretNames.ToConfigurationKey(SecretNames.DatabaseConnectionString).ShouldBe("EVIDGATE:DATABASE:CONNECTIONSTRING");
        SecretNames.ToConfigurationKey(SecretNames.MigratorConnectionString).ShouldBe("EVIDGATE_MIGRATOR:CONNECTIONSTRING");
    }

    [Fact]
    public async Task Configuration_Lookup_Of_The_Mapped_Key_Ignores_Case()
    {
        // A host (or test) that sets the key in its mixed-case spelling still
        // resolves through the upper-case mapped key: IConfiguration keys are
        // case-insensitive, which is what makes the case-preserving mapping safe.
        const string marker = "case-insensitive-lookup-marker";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Evidgate:Ci:Pepper"] = marker })
            .Build();
        var provider = new EnvironmentSecretProvider(
            name => configuration[SecretNames.ToConfigurationKey(name)]);

        configuration[SecretNames.ToConfigurationKey(SecretNames.CiPepper)].ShouldBe(marker);
        (await provider.GetSecretAsync(SecretNames.CiPepper, TestContext.Current.CancellationToken))
            .ShouldBe(marker);
    }

    [Fact]
    public void All_Declares_Every_Secret_Name()
    {
        SecretNames.All.ShouldContain(SecretNames.DatabaseConnectionString);
        SecretNames.All.ShouldContain(SecretNames.TasksSweepConnectionString);
        SecretNames.All.ShouldContain(SecretNames.CiSigningKey);
        SecretNames.All.ShouldContain(SecretNames.CiPepper);
        SecretNames.All.ShouldContain(SecretNames.CiPreviousPepper);
        SecretNames.All.ShouldContain(SecretNames.MigratorConnectionString);
    }
}
