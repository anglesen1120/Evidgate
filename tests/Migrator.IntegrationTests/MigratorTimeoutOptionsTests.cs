using System.Globalization;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The server-side timeout settings of the Migrator: the built-in defaults,
/// explicit disabling and the fail-closed environment parsing. No database is
/// touched, so these run without a container.
/// </summary>
public sealed partial class MigratorTimeoutOptionsTests
{
    private const string ValidConnectionString =
        "Host=127.0.0.1;Port=1;Database=evidgate;Username=evidgate_owner;Password=test-only";

    [Fact]
    public void New_Options_Carry_The_Server_Timeout_Defaults()
    {
        var options = new MigratorOptions { MigrationsPath = AppContext.BaseDirectory };

        options.LockTimeoutSeconds.ShouldBe(MigratorOptions.DefaultLockTimeoutSeconds);
        options.LockTimeoutSeconds.ShouldBe(5);
        options.StatementTimeoutSeconds.ShouldBe(MigratorOptions.DefaultStatementTimeoutSeconds);
        options.StatementTimeoutSeconds.ShouldBe(900);
    }

    [Fact]
    public void Unset_Variables_Take_The_Defaults()
    {
        var options = MigratorOptions.FromEnvironment(Env());

        options.LockTimeoutSeconds.ShouldBe(MigratorOptions.DefaultLockTimeoutSeconds);
        options.StatementTimeoutSeconds.ShouldBe(MigratorOptions.DefaultStatementTimeoutSeconds);
    }

    [Theory]
    [InlineData(MigratorOptions.LockTimeoutVariable, "abc")]
    [InlineData(MigratorOptions.LockTimeoutVariable, "-1")]
    [InlineData(MigratorOptions.LockTimeoutVariable, "1.5")]
    [InlineData(MigratorOptions.LockTimeoutVariable, "99999999999")]
    [InlineData(MigratorOptions.LockTimeoutVariable, "2147484")]
    [InlineData(MigratorOptions.LockTimeoutVariable, "2147483647")]
    [InlineData(MigratorOptions.LockTimeoutVariable, " 1 ")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, "abc")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, "-1")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, "1.5")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, "99999999999")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, "2147484")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, "2147483647")]
    [InlineData(MigratorOptions.StatementTimeoutVariable, " 1 ")]
    public void Invalid_Value_Fails_Closed_And_Names_The_Variable(string variable, string value)
    {
        var exception = Should.Throw<MigratorSetupException>(
            () => MigratorOptions.FromEnvironment(Env(variable, value)));

        exception.Message.ShouldContain(variable);
        exception.Errors.ShouldContain(error => error.Contains(variable, StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_Disables_Each_Timeout_Explicitly()
    {
        var lockDisabled = MigratorOptions.FromEnvironment(Env(MigratorOptions.LockTimeoutVariable, "0"));
        lockDisabled.LockTimeoutSeconds.ShouldBe(0);
        lockDisabled.StatementTimeoutSeconds.ShouldBe(MigratorOptions.DefaultStatementTimeoutSeconds);

        var statementDisabled = MigratorOptions.FromEnvironment(Env(MigratorOptions.StatementTimeoutVariable, "0"));
        statementDisabled.StatementTimeoutSeconds.ShouldBe(0);
        statementDisabled.LockTimeoutSeconds.ShouldBe(MigratorOptions.DefaultLockTimeoutSeconds);
    }

    [Theory]
    [InlineData(MigratorOptions.LockTimeoutVariable)]
    [InlineData(MigratorOptions.StatementTimeoutVariable)]
    public void Maximum_Server_Timeout_Is_Accepted(string variable)
    {
        var maximum = MigratorOptions.MaxServerTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var options = MigratorOptions.FromEnvironment(Env(variable, maximum));

        var configured = variable == MigratorOptions.LockTimeoutVariable
            ? options.LockTimeoutSeconds
            : options.StatementTimeoutSeconds;
        configured.ShouldBe(MigratorOptions.MaxServerTimeoutSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Blank_Values_Take_The_Defaults(string value)
    {
        var lockOptions = MigratorOptions.FromEnvironment(Env(MigratorOptions.LockTimeoutVariable, value));
        lockOptions.LockTimeoutSeconds.ShouldBe(MigratorOptions.DefaultLockTimeoutSeconds);

        var statementOptions = MigratorOptions.FromEnvironment(Env(MigratorOptions.StatementTimeoutVariable, value));
        statementOptions.StatementTimeoutSeconds.ShouldBe(MigratorOptions.DefaultStatementTimeoutSeconds);
    }

    private static Dictionary<string, string?> Env(string? variable = null, string? value = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = ValidConnectionString,
            [MigratorOptions.MigrationsPathVariable] = AppContext.BaseDirectory,
        };

        if (variable is not null)
        {
            environment[variable] = value;
        }

        return environment;
    }
}
