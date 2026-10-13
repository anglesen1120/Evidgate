// 06.3.2-T1.1 (AC4): FuzzRunOptions.FromEnvironment's strict parsing. An unset variable keeps the documented
// default, a valid value is honoured, and a variable that is SET but unreadable fails the run with an error
// naming it — never silently shrinking the run or replaying the default seed.
//
// The tests read through the environment-snapshot seam (a dictionary, the same shape as
// MigratorOptions.FromEnvironment), so they never mutate the real process environment and never race the
// process-environment path that HostileFuzzTests exercises with FuzzRunOptions.FromEnvironment().
using System.Globalization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.2-T2.1 (AC4): the strict parse of every <c>EVIDGATE_FUZZ_*</c> bound. A typo must fail the run rather
/// than pass for a smaller (still green) run or a replay of the default seed.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class FuzzRunOptionsTests
{
    /// <summary>An environment with no fuzz variable set keeps every documented default.</summary>
    [Fact]
    public void Unset_variables_keep_the_documented_defaults()
    {
        var options = Read();

        options.Seed.ShouldBe(FuzzRunOptions.DefaultSeed);
        options.BudgetSeconds.ShouldBe(FuzzRunOptions.SmokeBudgetSeconds);
        options.MaxIterations.ShouldBe(FuzzRunOptions.DefaultMaxIterations);
        options.PerInputMilliseconds.ShouldBe(FuzzRunOptions.DefaultPerInputMilliseconds);
        options.FindingsDirectory.ShouldBe(DefaultFindingsDirectory);
    }

    /// <summary>
    /// A variable that is present but empty is unset, exactly as an empty process variable is: it keeps the default
    /// rather than failing.
    /// </summary>
    [Fact]
    public void Empty_variable_is_treated_as_unset()
    {
        var options = Read(
            (FuzzRunOptions.SeedVariable, string.Empty),
            (FuzzRunOptions.BudgetVariable, string.Empty),
            (FuzzRunOptions.IterationsVariable, string.Empty),
            (FuzzRunOptions.PerInputVariable, string.Empty),
            (FuzzRunOptions.FindingsDirectoryVariable, string.Empty));

        options.Seed.ShouldBe(FuzzRunOptions.DefaultSeed);
        options.BudgetSeconds.ShouldBe(FuzzRunOptions.SmokeBudgetSeconds);
        options.MaxIterations.ShouldBe(FuzzRunOptions.DefaultMaxIterations);
        options.PerInputMilliseconds.ShouldBe(FuzzRunOptions.DefaultPerInputMilliseconds);
        options.FindingsDirectory.ShouldBe(DefaultFindingsDirectory);
    }

    /// <summary>
    /// A valid value for each variable is honoured, the iteration cap included: it is a <see cref="long"/>, so a
    /// value above <see cref="int.MaxValue"/> (the nightly backstop) must survive the parse.
    /// </summary>
    [Fact]
    public void Valid_values_are_honoured()
    {
        var options = Read(
            (FuzzRunOptions.SeedVariable, "123456789"),
            (FuzzRunOptions.BudgetVariable, "600"),
            (FuzzRunOptions.IterationsVariable, "4294967296"),
            (FuzzRunOptions.PerInputVariable, "250"),
            (FuzzRunOptions.FindingsDirectoryVariable, "/tmp/evidgate-findings"));

        options.Seed.ShouldBe(123_456_789UL);
        options.BudgetSeconds.ShouldBe(600);
        options.MaxIterations.ShouldBe(4_294_967_296L);
        options.PerInputMilliseconds.ShouldBe(250);
        options.FindingsDirectory.ShouldBe("/tmp/evidgate-findings");
    }

    /// <summary>The seed is unsigned, so both ends of the range are accepted.</summary>
    [Fact]
    public void Seed_boundary_values_are_honoured()
    {
        Read((FuzzRunOptions.SeedVariable, "0")).Seed.ShouldBe(0UL);
        Read((FuzzRunOptions.SeedVariable, ulong.MaxValue.ToString(CultureInfo.InvariantCulture)))
            .Seed.ShouldBe(ulong.MaxValue);
    }

    /// <summary>A set seed that is not an unsigned integer fails, naming the variable.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("18446744073709551616")] // ulong.MaxValue + 1
    public void Seed_set_but_invalid_fails_naming_the_variable(string value) =>
        Refused(FuzzRunOptions.SeedVariable, value, "an unsigned integer");

    /// <summary>A set budget that is not a positive integer fails, naming the variable.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void Budget_set_but_invalid_fails_naming_the_variable(string value) =>
        Refused(FuzzRunOptions.BudgetVariable, value, "a positive integer");

    /// <summary>A set iteration cap that is not a positive integer fails, naming the variable.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void Iterations_set_but_invalid_fails_naming_the_variable(string value) =>
        Refused(FuzzRunOptions.IterationsVariable, value, "a positive integer");

    /// <summary>A set per-input bound that is not a positive integer fails, naming the variable.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void Per_input_set_but_invalid_fails_naming_the_variable(string value) =>
        Refused(FuzzRunOptions.PerInputVariable, value, "a positive integer");

    private static string DefaultFindingsDirectory => Path.Combine(Path.GetTempPath(), "evidgate-fuzz-findings");

    /// <summary>Reads the options from the given variable snapshot through the environment seam.</summary>
    private static FuzzRunOptions Read(params (string Name, string Value)[] variables) =>
        FuzzRunOptions.FromEnvironment(
            variables.ToDictionary(variable => variable.Name, variable => (string?)variable.Value, StringComparer.Ordinal));

    /// <summary>Asserts the parse refuses the value, and the message names the variable and what it expected.</summary>
    private static void Refused(string name, string value, string expected)
    {
        var error = Should.Throw<InvalidOperationException>(() =>
        {
            _ = Read((name, value));
        });

        error.Message.ShouldContain(name);
        error.Message.ShouldContain($"'{value}'");
        error.Message.ShouldContain(expected);
    }
}
