using System.Globalization;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The command-line and configuration contract of the Migrator, exercised through
/// its real entry point (<see cref="MigratorHost"/>) without a database: the
/// argument and environment rules decide an exit code before any connection is
/// opened, and a wrong code here is what breaks a deploy gate (01.4.1 §5).
/// </summary>
public sealed class MigratorHostTests
{
    private const string UnreachableButValidConnectionString =
        "Host=127.0.0.1;Port=1;Database=evidgate;Username=evidgate_owner;Password=test-only";

    [Fact]
    public void Missing_Connection_String_Exits_With_Code_1()
    {
        var run = Run(Env(), "--status");

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain(MigratorOptions.ConnectionStringVariable);
        run.Error.ShouldContain("usage:");
        run.Output.ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_Connection_String_Exits_With_Code_1()
    {
        var run = Run(Env("Host=h;Port=not-a-number"), "--status");

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain(MigratorOptions.ConnectionStringVariable);
    }

    [Fact]
    public void Unknown_Argument_Exits_With_Code_1()
    {
        var run = Run(Env(UnreachableButValidConnectionString), "--apply");

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("unknown argument '--apply'");
    }

    [Fact]
    public void Two_Arguments_Exit_With_Code_1()
    {
        var run = Run(Env(UnreachableButValidConnectionString), "--status", "--dry-run");

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("at most one argument");
    }

    [Fact]
    public void Help_Prints_Usage_And_Exits_Zero()
    {
        var run = Run(Env(), "--help");

        run.ExitCode.ShouldBe(MigratorExitCodes.Success);
        run.Output.ShouldContain("usage: Evidgate.Migrator");
        run.Output.ShouldContain(MigratorOptions.ConnectionStringVariable);
        run.Output.ShouldContain(MigratorOptions.MigrationsPathVariable);
        run.Output.ShouldContain(MigratorOptions.CommandTimeoutVariable);
        run.Error.ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_Command_Timeout_Exits_With_Code_1()
    {
        var run = Run(Env(UnreachableButValidConnectionString, commandTimeout: "half a minute"), "--status");

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain(MigratorOptions.CommandTimeoutVariable);
    }

    /// <summary>
    /// The default is "no command timeout": a deploy-time statement must not be
    /// cut off at a driver default, and the value must reach the options.
    /// </summary>
    [Fact]
    public void Command_Timeout_Defaults_To_None_And_Is_Configurable()
    {
        var defaults = MigratorOptions.FromEnvironment(Env(UnreachableButValidConnectionString));
        defaults.CommandTimeoutSeconds.ShouldBe(MigratorOptions.DefaultCommandTimeoutSeconds);
        defaults.CommandTimeoutSeconds.ShouldBe(0);

        MigratorOptions.FromEnvironment(Env(UnreachableButValidConnectionString, commandTimeout: "5"))
            .CommandTimeoutSeconds.ShouldBe(5);
    }

    [Fact]
    public void Default_Migrations_Path_Resolves_To_The_Repository_Directory()
    {
        var options = MigratorOptions.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [MigratorOptions.ConnectionStringVariable] = UnreachableButValidConnectionString,
            });

        options.MigrationsPath.ShouldEndWith("migrations");
        Directory.Exists(options.MigrationsPath).ShouldBeTrue();
        File.Exists(Path.Combine(options.MigrationsPath, "0001_foundation_roles.sql")).ShouldBeTrue();
    }

    private static Dictionary<string, string?> Env(
        string? connectionString = null,
        string? commandTimeout = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.MigrationsPathVariable] = AppContext.BaseDirectory,
        };

        if (connectionString is not null)
        {
            environment[MigratorOptions.ConnectionStringVariable] = connectionString;
        }

        if (commandTimeout is not null)
        {
            environment[MigratorOptions.CommandTimeoutVariable] = commandTimeout;
        }

        return environment;
    }

    private static (int ExitCode, string Output, string Error) Run(
        Dictionary<string, string?> environment,
        params string[] args)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = MigratorHost.Run(args, output, error, environment);

        return (exitCode, output.ToString(), error.ToString());
    }
}
