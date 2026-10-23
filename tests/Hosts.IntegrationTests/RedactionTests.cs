using Evidgate.BuildingBlocks.Observability.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC4 (T1.1/T1.2, 00 §5.7 S3): the connection string bound from
/// <c>EVIDGATE__DATABASE__CONNECTIONSTRING</c> reaches no provider with its
/// password readable — including inside the rendered message, and including a
/// password that needs quoting because it contains the <c>;</c> separator
/// (Review#2).
/// </summary>
public sealed class RedactionTests
{
    private const string Secret = "Sup3r-Secret-P4ss";

    [Fact]
    public async Task ConnectionString_Masked()
    {
        var capture = new CaptureLoggerProvider();
        var connectionString =
            $"Host=db.internal;Port=5432;Username=evidgate_app;Password={Secret};Database=evidgate;Timeout=2";

        // The Api logs the bound connection string at boot (ApiHost); it must be
        // masked both in the structured value and in the rendered message.
        await using var factory = new EvidgateApiFactory(connectionString, capture);
        using var client = factory.CreateClient();

        using (var response = await client.GetAsync("/internal/health/live", TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
        }

        var logs = capture.Logs;
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(logs, log => log.Text.Contains(Secret, StringComparison.Ordinal));

        var boot = Assert.Single(logs, log => log.Text.Contains("Boot configuration bound", StringComparison.Ordinal));
        Assert.Contains("Password=***", boot.Text, StringComparison.Ordinal);
        Assert.Contains("db.internal", boot.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quoted_Password_With_Semicolon_Fully_Masked()
    {
        // Npgsql accepts and re-renders a password containing ';' in double quotes;
        // an unquoted-only pattern would mask up to the first ';' and log the tail.
        const string tail = "Secret-P4ss";
        var capture = new CaptureLoggerProvider();
        var connectionString =
            $"Host=db.internal;Port=5432;Username=evidgate_app;Password=\"Sup3r;{tail}\";Database=evidgate;Timeout=2";

        await using var factory = new EvidgateApiFactory(connectionString, capture);
        using var client = factory.CreateClient();

        using (var response = await client.GetAsync("/internal/health/live", TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
        }

        var logs = capture.Logs;
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(logs, log => log.Text.Contains(tail, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, log => log.Text.Contains("Sup3r;", StringComparison.Ordinal));

        var boot = Assert.Single(logs, log => log.Text.Contains("Boot configuration bound", StringComparison.Ordinal));
        Assert.Contains("Password=***", boot.Text, StringComparison.Ordinal);
    }
}

/// <summary>
/// AC4 (00 §5.7 S3): the redaction list itself, exercised on the shapes that
/// reach a log line. The <c>Authorization</c> case lives here rather than in a
/// production log path, so a bearer token never depends on redaction to stay
/// out of the logs.
/// </summary>
public sealed class SecretRedactorTests
{
    private static readonly SecretRedactor Redactor = new();

    [Theory]
    [InlineData("Password=Sup3r-Secret-P4ss", "Sup3r-Secret-P4ss")]
    [InlineData("Password=\"Sup3r;Secret-P4ss\"", "Secret-P4ss")]
    [InlineData("Password='Sup3r;Secret-P4ss'", "Secret-P4ss")]
    [InlineData("Pwd=a;b", "a")]
    [InlineData("EVIDGATE__X__TOKEN=abc.def", "abc.def")]
    public void Secret_Assignments_Are_Masked(string text, string secret)
    {
        var redacted = Redactor.RedactText(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorization_Header_Is_Masked()
    {
        var redacted = Redactor.RedactText("auth Authorization: Bearer abc.def.ghi");

        Assert.DoesNotContain("abc.def.ghi", redacted, StringComparison.Ordinal);
        Assert.Contains("Authorization: " + SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_Secret_Text_Is_Unchanged()
    {
        const string text = "Host=db.internal;Port=5432;Username=evidgate_app;Database=evidgate";
        Assert.Equal(text, Redactor.RedactText(text));
    }
}
