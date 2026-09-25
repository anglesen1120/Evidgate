using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The Migrator's secret handling (00 §5.7 S3). These are unit-level checks of
/// the code every message and log line passes through, because a connection
/// string that reaches a message, a Shouldly failure or a debugger dump is a
/// leaked credential — and the interesting case is a password containing exactly
/// the characters a naive pattern stops at.
/// </summary>
public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("Host=h;Port=5432;Database=d;Username=u;Password=plain", "plain")]
    [InlineData("Host=h;Port=5432;Database=d;Username=u;Password=\"a;b\"", "a;b")]
    [InlineData("Host=h;Port=5432;Database=d;Username=u;Password='a;b'", "a;b")]
    [InlineData("Host=h;Port=5432;Database=d;Username=u;Password=\" p w \"", " p w ")]
    public void Masks_A_Password_That_Contains_Semicolons_Or_Quotes(string connectionString, string secret)
    {
        var redacted = SecretRedactor.Redact(connectionString, secret);

        redacted.ShouldNotContain(secret);
        redacted.ShouldContain("<redacted>");

        // The non-secret parts survive, so the message stays useful.
        redacted.ShouldContain("Host=h");
        redacted.ShouldContain("Database=d");
    }

    [Theory]
    [InlineData("Npgsql: failed for Host=h;Password=other-secret;Database=d", "other-secret", "Password=<redacted>")]
    [InlineData("Npgsql: failed for Host=h;Password='a''b;c';Database=d", "b;c", "Password=<redacted>")]
    [InlineData("Npgsql: failed for Host=h;Password=\"x;y\"\"z\";Database=d", "z\"", "Password=<redacted>")]
    [InlineData("Npgsql: failed for Host=h;Pwd=other-secret;Database=d", "other-secret", "Pwd=<redacted>")]
    public void Masks_The_Password_Key_Even_When_The_Secret_Is_Unknown(string text, string leak, string masked)
    {
        // The shape a driver echoes back in an exception, with the secret not known.
        var redacted = SecretRedactor.Redact(text, secret: null);

        redacted.ShouldNotContain(leak);
        redacted.ShouldContain(masked);
        redacted.ShouldContain(";Database=d");
    }

    [Fact]
    public void Masks_An_Unquoted_Secret_That_Contains_A_Semicolon()
    {
        // Unquoted with an embedded ';' is ambiguous by construction in the
        // connection-string grammar, which is why the known secret is applied first.
        var redacted = SecretRedactor.Redact("Password=a;b;Database=d", "a;b");

        redacted.ShouldNotContain("a;b");
        redacted.ShouldContain("Database=d");
    }

    [Fact]
    public void Empty_And_Unset_Input_Stay_Empty()
    {
        SecretRedactor.Redact(null, "secret").ShouldBeEmpty();
        SecretRedactor.Redact(string.Empty, null).ShouldBeEmpty();
    }

    [Fact]
    public void Options_Print_Only_The_Target_Not_The_Credential()
    {
        var options = new MigratorOptions
        {
            ConnectionString = "Host=db.internal;Port=5432;Database=evidgate;Username=evidgate_owner;Password=top-secret",
            MigrationsPath = "/srv/evidgate/migrations",
        };

        // A record's generated ToString would print every property, secret included.
        var printed = $"{options}";

        printed.ShouldNotContain("top-secret");
        printed.ShouldContain("db.internal:5432/evidgate");
        printed.ShouldContain("/srv/evidgate/migrations");
        options.DescribeTarget().ShouldBe("db.internal:5432/evidgate as 'evidgate_owner'");
    }

    [Fact]
    public void A_Rejected_Connection_String_Does_Not_Leak_Its_Password()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = "Host=h;Port=not-a-number;Password=top-secret",
            [MigratorOptions.MigrationsPathVariable] = AppContext.BaseDirectory,
        };

        var error = Should.Throw<MigratorSetupException>(() => MigratorOptions.FromEnvironment(environment));

        error.Errors.ShouldAllBe(message => !message.Contains("top-secret", StringComparison.Ordinal));
        error.Errors.ShouldContain(message => message.Contains(MigratorOptions.ConnectionStringVariable, StringComparison.Ordinal));
    }
}
