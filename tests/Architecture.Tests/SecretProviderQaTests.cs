using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>10.1.1-T2: binder bypass and provider input boundaries.</summary>
public sealed class SecretProviderQaTests
{
    [Fact]
    public void Binder_Cannot_Populate_Any_Runtime_Secret_Property()
    {
        const string marker = "qa-value-not-a-credential";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = marker,
            ["Tasks:SweepConnectionString"] = marker,
            ["Ci:SigningKey"] = marker,
            ["Ci:Pepper"] = marker,
            ["Ci:PreviousPepper"] = marker,
            ["Tasks:BatchSize"] = "17",
        }).Build();
        var database = new DatabaseOptions();
        var tasks = new TasksOptions();
        var ciType = Type.GetType("Evidgate.Modules.Identity.Options.CiAuthOptions, Evidgate.Modules.Identity", throwOnError: true)!;
        var ci = Activator.CreateInstance(ciType)!;
        configuration.GetSection("Database").Bind(database);
        configuration.GetSection("Tasks").Bind(tasks);
        configuration.GetSection("Ci").Bind(ci);

        string.IsNullOrEmpty(database.ConnectionString).ShouldBeTrue("the binder must not write the database secret");
        string.IsNullOrEmpty(tasks.SweepConnectionString).ShouldBeTrue("the binder must not write the sweep secret");
        foreach (var property in new[] { "SigningKey", "Pepper", "PreviousPepper" })
        {
            string.IsNullOrEmpty((string?)ciType.GetProperty(property)!.GetValue(ci))
                .ShouldBeTrue("the binder must not write CI secret properties");
        }
        tasks.BatchSize.ShouldBe(17);
    }

    [Theory]
    [InlineData("\t\r\n")]
    [InlineData("\u2003\u00a0")]
    public async Task Whitespace_Only_Secret_Is_Missing(string whitespace)
    {
        var provider = new EnvironmentSecretProvider(_ => whitespace);
        (await provider.GetSecretAsync(SecretNames.DatabaseConnectionString, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("evidgate__ci__pepper")]
    public async Task Unknown_Name_Is_Rejected_Before_Lookup(string name)
    {
        var called = false;
        var provider = new EnvironmentSecretProvider(_ =>
        {
            called = true;
            return "qa-sensitive-marker";
        });
        var error = await Should.ThrowAsync<ArgumentException>(async () => await provider.GetSecretAsync(name));
        called.ShouldBeFalse();
        error.ParamName.ShouldBe("name");
        error.Message.Contains("qa-sensitive-marker", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task Nonblank_Secret_Preserves_Leading_And_Trailing_Spaces()
    {
        var value = "  qa-sensitive-marker  ";
        var provider = new EnvironmentSecretProvider(_ => value);
        var resolved = await provider.GetSecretAsync(SecretNames.CiPepper, TestContext.Current.CancellationToken);
        (resolved == value).ShouldBeTrue("nonblank credentials must not be silently trimmed");
    }
}
