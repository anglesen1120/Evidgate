using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The provider key: presented from the secret provider on every request and absent from every surface a test can observe —
/// the request the adapter builds, the result it returns, the options snapshot and the log. A key that resolves to nothing
/// sends no request at all.
/// </summary>
public sealed partial class HealingLlmApiKeyHandlerTests
{
    [Fact]
    public async Task The_Key_Is_Presented_From_The_Secret_Provider_And_Reaches_No_Log()
    {
        // Built at run time, so the test file carries no key-shaped literal.
        var key = "provider-key-" + Guid.NewGuid().ToString("N");
        // Stated at the key's own configuration slot: binding must still leave the member unbound.
        var configuredKey = "configured-key-" + Guid.NewGuid().ToString("N");
        var endpoint = new RecordingHandler(HttpStatusCode.OK, """{"content":"{}","tokens":2,"cost":0.5}""");
        var capture = new CaptureLoggerProvider();

        await using var provider = Compose(capture, new FixedSecrets(key), endpoint, configuredKey).BuildServiceProvider();

        var result = await provider
            .GetRequiredService<HttpHealingLlmProvider>()
            .InvokeAsync(Request(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealingLlmStatuses.Ok);

        var sent = endpoint.Requests.ShouldHaveSingleItem();
        sent.Headers.Authorization.ShouldNotBeNull().Parameter.ShouldBe(key);
        sent.RequestUri!.ToString().ShouldNotContain(key);
        sent.Headers
            .Where(header => !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .Any(value => value.Contains(key, StringComparison.Ordinal))
            .ShouldBeFalse("the key travels in the Authorization header and nowhere else");

        // The capture is live: a line written through the host's own logger reaches it, so the absence of the key below
        // is a fact about every line the call wrote, not an empty list.
        var probeLogger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("HealingProviderKeyTests");
        Log.CaptureProbe(probeLogger, "healing-provider-capture-live");
        capture.Logs.ShouldContain(log => log.Text.Contains("healing-provider-capture-live", StringComparison.Ordinal));
        capture.Logs.ShouldNotContain(log => log.Text.Contains(key, StringComparison.Ordinal));
        result.ToString().ShouldNotContain(key);

        // The key is read per request: the options snapshot holds the secret's name, and the value stated at its own
        // configuration slot stays unbound because only the secret provider may supply it.
        var selection = provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value;
        selection.ApiKeySecret.ShouldBe(SecretNames.HealingLlmApiKey);
        selection.ApiKey.ShouldBeNull();
        (selection.ToString() ?? string.Empty).ShouldNotContain(configuredKey);
    }

    [Fact]
    public async Task A_Foreign_Target_Through_The_Composed_Client_Reads_No_Key_And_Sends_Nothing()
    {
        var endpoint = new RecordingHandler();
        var secrets = new CountingSecrets();
        await using var provider = Compose(new CaptureLoggerProvider(), secrets, endpoint).BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpHealingLlmProvider));

        await Should.ThrowAsync<InvalidOperationException>(() => client.PostAsync(
            "https://elsewhere.invalid/v1/llm/generate",
            new StringContent("{}"),
            TestContext.Current.CancellationToken));

        secrets.Reads.ShouldBe(0);
        endpoint.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Key_That_Resolves_To_Nothing_Sends_No_Request()
    {
        var endpoint = new RecordingHandler(HttpStatusCode.OK, "{}");
        var capture = new CaptureLoggerProvider();

        await using var provider = Compose(capture, new FixedSecrets(null), endpoint).BuildServiceProvider();

        var result = await provider
            .GetRequiredService<HttpHealingLlmProvider>()
            .InvokeAsync(Request(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealingLlmStatuses.Error);
        endpoint.Requests.ShouldBeEmpty();
        capture.Logs.ShouldNotContain(log => log.Text.Contains("Bearer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Refusal_Names_The_Configuration_Key_And_No_Secret_Value()
    {
        var handler = new HealingLlmApiKeyHandler(
            new FixedSecrets(null),
            new HealingProviderOptions { Provider = "http", ApiKeySecret = SecretNames.HealingLlmApiKey })
        {
            InnerHandler = new RecordingHandler(HttpStatusCode.OK, "{}"),
        };
        using var invoker = new HttpMessageInvoker(handler);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/v1/llm/generate"),
            TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(HealingProviderKeys.ApiKeySecret);
        // The configuration key's own spelling contains the secret name as a prefix, so it is removed before the check.
        failure.Message.Replace("EVIDGATE__HEALING__LLM__APIKEYSECRET", string.Empty, StringComparison.Ordinal)
            .ShouldNotContain(SecretNames.HealingLlmApiKey);
        failure.Message.ShouldNotContain("Bearer");
    }

    /// <summary>
    /// A header value cannot carry CR, LF or NUL, so a key holding one is refused through the unset-secret path: no
    /// request leaves and the value never reaches the message.
    /// </summary>
    /// <param name="codeUnit">
    /// The UTF-16 code unit of the control character the resolved key carries, so no test name holds a raw control
    /// character.
    /// </param>
    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x0A)]
    [InlineData(0x00)]
    public async Task A_Key_Carrying_A_Control_Character_Refuses_And_Sends_Nothing(int codeUnit)
    {
        // Built at run time, so the canary is absent from this file and the assertion below can only pass if the
        // refusal leaves it out of the message.
        var canary = "provider-key-" + Guid.NewGuid().ToString("N");
        var endpoint = new RecordingHandler(HttpStatusCode.OK, "{}");
        var handler = new HealingLlmApiKeyHandler(
            new FixedSecrets(canary + (char)codeUnit + "X-Injected: 1"),
            new HealingProviderOptions { Provider = "http", ApiKeySecret = SecretNames.HealingLlmApiKey })
        {
            InnerHandler = endpoint,
        };
        using var invoker = new HttpMessageInvoker(handler);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/v1/llm/generate"),
            TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(HealingProviderKeys.ApiKeySecret);
        failure.Message.ShouldNotContain(canary);
        failure.Message.ShouldNotContain("X-Injected");
        endpoint.Requests.ShouldBeEmpty();
    }

    /// <summary>The module's composition with the HTTP adapter selected and the recorded endpoint as the transport.</summary>
    /// <param name="capture">The capture the host's logging is routed through.</param>
    /// <param name="secrets">The secret provider the handler resolves the key from.</param>
    /// <param name="endpoint">The handler that stands in for the provider's endpoint.</param>
    /// <param name="configuredKey">The value stated at the key's own configuration slot, or null to leave it unset.</param>
    private static ServiceCollection Compose(
        CaptureLoggerProvider capture,
        ISecretProvider secrets,
        HttpMessageHandler endpoint,
        string? configuredKey = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Healing:Llm:Provider"] = "http",
            ["EVIDGATE:Healing:Llm:BaseAddress"] = "https://provider.invalid/v1/",
            ["EVIDGATE:Healing:Llm:ApiKeySecret"] = SecretNames.HealingLlmApiKey,
        };

        if (configuredKey is not null)
        {
            // The slot exists only so a host can state it; binding must leave it unbound.
            settings["EVIDGATE:Healing:Llm:ApiKey"] = configuredKey;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = new TestHostEnvironment();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(capture));
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton(secrets);
        services.AddHealingModule(configuration, environment);

        // The module configures the client, the key handler and the resilience pipeline; only the transport is replaced,
        // so no socket is opened.
        services.AddHttpClient<HttpHealingLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => endpoint);

        return services;
    }

    private static HealingLlmRequest Request() => new(
        Guid.CreateVersion7(),
        HealingLlmPurposes.TriageHypothesis,
        Guid.CreateVersion7(),
        CandidateId: null,
        Model: "healing-test-model",
        PromptVersion: "v1",
        Payload: "Design the checkout checklist for the shop.",
        ResponseSchema: null);

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "capture probe {Probe}")]
        public static partial void CaptureProbe(ILogger logger, string probe);
    }

    private sealed class CountingSecrets : ISecretProvider
    {
        internal int Reads { get; private set; }

        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult<string?>("unused");
        }
    }

    /// <summary>A secret provider that answers every name with the same value.</summary>
    private sealed class FixedSecrets(string? value) : ISecretProvider
    {
        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(value);
    }

    /// <summary>Records what reached the transport and answers one fixed response.</summary>
    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        internal IReadOnlyList<HttpRequestMessage> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>The smallest <see cref="IHostEnvironment"/> the module's selection reads: its name.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Evidgate.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
