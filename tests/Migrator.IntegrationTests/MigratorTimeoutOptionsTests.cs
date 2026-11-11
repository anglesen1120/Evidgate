using System.Globalization;
using Evidgate.Migrator;
using Npgsql;
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
    private static readonly string ValidConnectionString = new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = 1,
        Database = "evidgate",
        Username = "evidgate_owner",
        Password = nameof(ValidConnectionString),
    }.ConnectionString;

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
    public void Session_Connection_String_Sends_Both_Limits_As_Startup_Options()
    {
        var options = new MigratorOptions
        {
            MigrationsPath = AppContext.BaseDirectory,
            ConnectionString = ValidConnectionString,
            LockTimeoutSeconds = 7,
            StatementTimeoutSeconds = 0,
        };

        new NpgsqlConnectionStringBuilder(options.SessionConnectionString).Options
            .ShouldBe("-c lock_timeout=7000ms -c statement_timeout=0ms");
    }

    [Fact]
    public void An_Operator_Options_Value_Is_Kept_And_The_Configured_Limits_Come_Last()
    {
        var operatorOptions = new NpgsqlConnectionStringBuilder(ValidConnectionString)
        {
            Options = "-c search_path=public -c lock_timeout=1ms",
        }.ConnectionString;
        var options = new MigratorOptions { MigrationsPath = AppContext.BaseDirectory, ConnectionString = operatorOptions };

        new NpgsqlConnectionStringBuilder(options.SessionConnectionString).Options
            .ShouldBe("-c search_path=public -c lock_timeout=1ms -c lock_timeout=5000ms -c statement_timeout=900000ms");
    }

    [Fact]
    public void Inherited_Pgoptions_Are_Kept_When_The_Connection_String_Has_No_Options()
    {
        // Process-wide, but this class's tests run one after another and the value is inert to the server.
        var previous = Environment.GetEnvironmentVariable("PGOPTIONS");
        Environment.SetEnvironmentVariable("PGOPTIONS", "-c application_name=pgoptions_probe");
        try
        {
            var options = new MigratorOptions { MigrationsPath = AppContext.BaseDirectory, ConnectionString = ValidConnectionString };

            new NpgsqlConnectionStringBuilder(options.SessionConnectionString).Options
                .ShouldBe("-c application_name=pgoptions_probe -c lock_timeout=5000ms -c statement_timeout=900000ms");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGOPTIONS", previous);
        }
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
