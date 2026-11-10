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
        var endpoint = new RecordingHandler(HttpStatusCode.OK, """{"content":"{}","tokens":2,"cost":0.5}""");
        var capture = new CaptureLoggerProvider();

        await using var provider = Compose(capture, new FixedSecrets(key), endpoint).BuildServiceProvider();

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
        Log.CaptureProbe(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("HealingProviderKeyTests"),
            "healing-provider-capture-live");
        capture.Logs.ShouldContain(log => log.Text.Contains("healing-provider-capture-live", StringComparison.Ordinal));
        capture.Logs.ShouldNotContain(log => log.Text.Contains(key, StringComparison.Ordinal));
        result.ToString().ShouldNotContain(key);

        // The key is read per request: the options snapshot holds the secret's name and never its value.
        provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value.ApiKey.ShouldBeNull();
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
        failure.Message.ShouldNotContain(SecretNames.HealingLlmApiKey);
        failure.Message.ShouldNotContain("Bearer");
    }

    /// <summary>The module's composition with the HTTP adapter selected and the recorded endpoint as the transport.</summary>
    /// <param name="capture">The capture the host's logging is routed through.</param>
    /// <param name="secrets">The secret provider the handler resolves the key from.</param>
    /// <param name="endpoint">The handler that stands in for the provider's endpoint.</param>
    private static ServiceCollection Compose(
        CaptureLoggerProvider capture,
        ISecretProvider secrets,
        HttpMessageHandler endpoint)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Healing:Llm:Provider"] = "http",
            ["EVIDGATE:Healing:Llm:BaseAddress"] = "https://provider.invalid/v1/",
            ["EVIDGATE:Healing:Llm:ApiKeySecret"] = SecretNames.HealingLlmApiKey,
        };

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

    /// <summary>A secret provider that answers every name with the same value.</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "capture probe {Probe}")]
        public static partial void CaptureProbe(ILogger logger, string probe);
    }

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
