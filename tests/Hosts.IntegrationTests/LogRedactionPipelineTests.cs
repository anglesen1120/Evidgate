using System.Collections.Concurrent;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.4 / AC2: what a collector would receive from the real logging pipeline carries no
/// secret — not in a state pair, not in a rendered template argument, not in a scope value, not in
/// a nested dictionary, not in an exception message — and the redaction list covers the shapes the
/// task names: <c>password</c>, <c>authorization</c>, <c>cookie</c>/<c>set-cookie</c>, <c>token</c>,
/// <c>secret</c>, <c>api_key</c>, a <c>__Host-</c> cookie value and an <c>EVIDGATE_*</c> key that
/// names a secret.
/// </summary>
/// <remarks>
/// The capture is the deployment's own: the host is booted through <c>AddEvidgateTelemetry</c>, so the
/// redacting logger factory and the export-side <c>LogRedactionProcessor</c> are in the path, and the
/// capture processor is registered after both. Each test proves the capture is alive with a value that
/// is not a secret before it asserts an absence.
/// </remarks>
public sealed partial class LogRedactionPipelineTests
{
    /// <summary>A password-shaped canary; the same value the suite's other redaction tests use.</summary>
    private const string PasswordCanary = "Sup3r-Secret-P4ss";

    /// <summary>A token-shaped canary.</summary>
    private const string TokenCanary = "abc.def.ghi";

    /// <summary>A cookie value with no secret word in it — only its <c>__Host-</c> name says what it is.</summary>
    private const string CookieCanary = "opaque-cookie-value";

    /// <summary>A value that must survive, so "nothing found" is never asserted against an empty capture.</summary>
    private const string VisibleValue = "visible-not-a-secret";

    /// <summary>The category of this suite's probe logger; the capture keeps nothing else.</summary>
    private const string ProbeCategory = "Hosts.IntegrationTests.LogRedactionProbe";

    private const string AuthorizationHeader = "Bearer " + TokenCanary;

    private const string CookieHeader = "Cookie: __Host-session=" + CookieCanary + "; theme=dark";

    private static readonly Dictionary<string, object?> NestedPayload = new(StringComparer.Ordinal)
    {
        ["Password"] = PasswordCanary,
        ["Visible"] = VisibleValue,
        ["nested"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = TokenCanary },
    };

    private static readonly Dictionary<string, object> ScopedValues = new(StringComparer.Ordinal)
    {
        ["PASSWORD"] = PasswordCanary,
        ["__Host-session"] = CookieCanary,
        ["Visible"] = VisibleValue,
    };

    [Fact]
    public void A_Key_That_Names_A_Secret_Is_Erased_Whatever_Its_Casing()
    {
        var redactor = new SecretRedactor();

        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("Password", PasswordCanary));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("PASSWORD", PasswordCanary));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("Authorization", AuthorizationHeader));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("Set-Cookie", "sid=" + CookieCanary));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("api_key", TokenCanary));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("Api_Key", TokenCanary));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("EVIDGATE__SESSION__PEPPER", CookieCanary));
        Assert.Equal(VisibleValue, redactor.RedactValue("Visible", VisibleValue));
    }

    [Fact]
    public void A_Host_Cookie_Name_Is_A_Secret_Even_Though_Its_Name_Says_Nothing()
    {
        var redactor = new SecretRedactor();

        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("__Host-session", CookieCanary));
        Assert.Equal(SecretRedactor.Mask, redactor.RedactValue("__Secure-token", CookieCanary));
        Assert.DoesNotContain(
            CookieCanary,
            redactor.RedactText("Cookie: __Host-session=" + CookieCanary + "; theme=dark"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            CookieCanary,
            redactor.RedactText("__Host-session=" + CookieCanary + "; theme=dark"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_Connection_String_Keeps_Its_Host_And_Loses_Its_Password()
    {
        var redacted = new SecretRedactor()
            .RedactValue("ConnectionString", "Host=db.internal;Port=5432;Database=evidgate;Password=" + PasswordCanary);

        Assert.DoesNotContain(PasswordCanary, redacted, StringComparison.Ordinal);
        Assert.Contains("Host=db.internal", redacted, StringComparison.Ordinal);
        Assert.Contains("Password=" + SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Nested_Dictionary_Is_Walked_By_Key()
    {
        var redacted = new SecretRedactor().RedactValue("Payload", NestedPayload);

        Assert.DoesNotContain(PasswordCanary, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenCanary, redacted, StringComparison.Ordinal);
        Assert.Contains(VisibleValue, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Exception_With_A_Secret_Is_Wrapped_And_Keeps_Its_Type()
    {
        var original = new InvalidOperationException("login failed: Password=" + PasswordCanary);
        var redacted = new SecretRedactor().RedactException(original);

        Assert.NotNull(redacted);
        Assert.NotSame(original, redacted);
        Assert.Contains("System.InvalidOperationException", redacted.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordCanary, redacted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordCanary, redacted.ToString(), StringComparison.Ordinal);
        Assert.Contains("Password=" + SecretRedactor.Mask, redacted.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Clean_Exception_Is_Passed_Through_Unchanged()
    {
        var original = new InvalidOperationException("nothing here is a secret");

        Assert.Same(original, new SecretRedactor().RedactException(original));
    }

    [Fact]
    public async Task Exported_Attributes_Scopes_And_Template_Arguments_Carry_No_Secret()
    {
        await using var harness = await LogPipelineHarness.StartAsync();
        var logger = harness.CreateLogger();

        Log.LoginProbe(logger, PasswordCanary, AuthorizationHeader, TokenCanary, TokenCanary);
        Log.NestedState(logger, NestedPayload);
        Log.Header(logger, CookieHeader);

        using (logger.BeginScope(ScopedValues))
        {
            Log.Scoped(logger, VisibleValue);
        }

        await harness.WaitForAsync(expected: 4);

        var text = harness.AllText();
        Assert.Contains(VisibleValue, text, StringComparison.Ordinal);
        foreach (var canary in new[] { PasswordCanary, TokenCanary, CookieCanary })
        {
            Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
        }

        // The scope value is redacted before any provider sees it: the capture below is a provider
        // inside the same logging pipeline, so it proves the decorator, not the export processor.
        var scopeText = harness.ScopeText();
        Assert.Contains(VisibleValue, scopeText, StringComparison.Ordinal);
        foreach (var canary in new[] { PasswordCanary, CookieCanary })
        {
            Assert.DoesNotContain(canary, scopeText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_Exported_Exception_Keeps_Its_Original_Type_And_Loses_Its_Message_Secret()
    {
        await using var harness = await LogPipelineHarness.StartAsync();
        var logger = harness.CreateLogger();

        Log.Handler(
            logger,
            new InvalidOperationException("connect failed: Password=" + PasswordCanary),
            VisibleValue);

        await harness.WaitForAsync(expected: 1);

        var record = Assert.Single(harness.Records);
        Assert.Contains(VisibleValue, record.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordCanary, record.Text, StringComparison.Ordinal);
        Assert.Equal(
            "System.InvalidOperationException",
            record.Attributes.Single(pair => pair.Key == "exception.type").Value as string);
        Assert.Contains(
            "Password=" + SecretRedactor.Mask,
            record.Attributes.Single(pair => pair.Key == "exception.message").Value as string,
            StringComparison.Ordinal);
    }

    /// <summary>The source-generated logging calls of this suite (CA1848/CA1873: no format strings at the call site).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "login {Password} {Authorization} {ApiKey} {Token}")]
        public static partial void LoginProbe(ILogger logger, string password, string authorization, string apiKey, string token);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "nested {Payload}")]
        public static partial void NestedState(ILogger logger, Dictionary<string, object?> payload);

        [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "headers {Header}")]
        public static partial void Header(ILogger logger, string header);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "scoped line {VisibleScoped}")]
        public static partial void Scoped(ILogger logger, string visibleScoped);

        [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "handler {VisibleHandler}")]
        public static partial void Handler(ILogger logger, Exception exception, string visibleHandler);
    }

    /// <summary>One exported record, already flattened to the text a collector would see.</summary>
    private sealed record CapturedRecord(IReadOnlyList<KeyValuePair<string, object?>> Attributes, string Text);

    /// <summary>The capture the host installs after the redaction processor.</summary>
    private sealed class CapturingLogProcessor(string category) : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<CapturedRecord> _records = new();

        public IReadOnlyList<CapturedRecord> Records => _records.ToArray();

        public override void OnEnd(LogRecord record)
        {
            // The host's own lifecycle logs (Microsoft.Hosting.Lifetime) flow through this same
            // pipeline, so the capture keeps only the probe category: a count assertion then means
            // "my records arrived", not "the host said something".
            if (!string.Equals(record.CategoryName, category, StringComparison.Ordinal))
            {
                return;
            }

            var attributes = record.Attributes?.ToArray() ?? [];

            var parts = new List<string>();
            if (record.FormattedMessage is { } formatted)
            {
                parts.Add(formatted);
            }

            if (record.Body is not null)
            {
                parts.Add(record.Body.ToString() ?? string.Empty);
            }

            parts.AddRange(attributes.Select(pair => $"{pair.Key}={pair.Value}"));
            if (record.Exception is { } exception)
            {
                parts.Add(exception.ToString());
            }

            _records.Enqueue(new CapturedRecord(attributes, string.Join(" | ", parts)));
        }
    }

    /// <summary>Captures the scope state a provider receives, so scope redaction is proven where it happens.</summary>
    private sealed class ScopeCapturingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _scopes = new();

        public string Text() => string.Join("\n", _scopes);

        public ILogger CreateLogger(string categoryName) => new ScopeLogger(categoryName, _scopes);

        public void Dispose()
        {
        }

        private sealed class ScopeLogger(string category, ConcurrentQueue<string> scopes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                if (!string.Equals(category, LogRedactionPipelineTests.ProbeCategory, StringComparison.Ordinal))
                {
                    return null;
                }

                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    var pairText = string.Join(", ", pairs.Select(pair => $"{pair.Key}={pair.Value}"));
                    scopes.Enqueue(pairText);
                }

                scopes.Enqueue(state.ToString() ?? string.Empty);

                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // The scope capture is the only interest of this provider.
            }
        }
    }

    /// <summary>The started host, the capture it feeds, and the logger factory the deployment built.</summary>
    private sealed class LogPipelineHarness : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly ILoggerFactory _loggerFactory;
        private readonly CapturingLogProcessor _processor;
        private readonly ScopeCapturingProvider _scopeProvider;

        private LogPipelineHarness(
            IHost host,
            ILoggerFactory loggerFactory,
            CapturingLogProcessor processor,
            ScopeCapturingProvider scopeProvider)
        {
            _host = host;
            _loggerFactory = loggerFactory;
            _processor = processor;
            _scopeProvider = scopeProvider;
        }

        public IReadOnlyList<CapturedRecord> Records => _processor.Records;

        public string AllText() => string.Join("\n", Records.Select(record => record.Text));

        public string ScopeText() => _scopeProvider.Text();

        public ILogger CreateLogger() => _loggerFactory.CreateLogger(LogRedactionPipelineTests.ProbeCategory);

        public static async Task<LogPipelineHarness> StartAsync()
        {
            var builder = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["EVIDGATE:Telemetry:SampleRatio"] = "1" });

            var scopeProvider = new ScopeCapturingProvider();
            builder.Logging.AddProvider(scopeProvider);

            builder.AddEvidgateTelemetry("evidgate-test");

            var processor = new CapturingLogProcessor(LogRedactionPipelineTests.ProbeCategory);
            builder.Services.Configure<OpenTelemetryLoggerOptions>(options => options.AddProcessor(processor));

            var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);

            return new LogPipelineHarness(
                host,
                host.Services.GetRequiredService<ILoggerFactory>(),
                processor,
                scopeProvider);
        }

        public async Task WaitForAsync(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && Records.Count < expected)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.True(
                Records.Count >= expected,
                $"only {Records.Count} of {expected} log records reached the capture — it proves nothing");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(TestContext.Current.CancellationToken);
            _host.Dispose();
        }
    }
}
