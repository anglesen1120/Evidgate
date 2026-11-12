using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// What the healing adapter puts on the wire and where it may send it: the output ceiling it asks the provider to stop at,
/// the single endpoint a request may reach, and the public-host rule on the configured base address.
/// </summary>
public sealed class HealingLlmEgressTests
{
    private static readonly Uri Base = new("https://api.provider.example/v1/");

    [Fact]
    public async Task The_Reserved_Output_Ceiling_Is_Sent_As_Max_Tokens()
    {
        var handler = new Capture();
        using var client = new HttpClient(handler) { BaseAddress = Base };
        var provider = new HttpHealingLlmProvider(
            client,
            Options.Create(new HealingLlmOptions { ReservedOutputTokens = 77 }));

        var result = await provider.InvokeAsync(
            new HealingLlmRequest(
                Guid.NewGuid(),
                HealingLlmPurposes.AuthoringGeneration,
                null,
                null,
                "m",
                "v1",
                "{}",
                null),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealingLlmStatuses.Ok);
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(77);
    }

    [Theory]
    [InlineData("https://api.provider.example/v1/llm/generate", true)]
    [InlineData("https://API.provider.example/v1/llm/generate", true)]
    [InlineData("https://api.provider.example/v2/llm/generate", false)]
    [InlineData("https://evil.example/v1/llm/generate", false)]
    [InlineData("https://api.provider.example:8443/v1/llm/generate", false)]
    [InlineData("http://api.provider.example/v1/llm/generate", false)]
    [InlineData("https://user:pw@api.provider.example/v1/llm/generate", false)]
    public async Task A_Request_Leaves_Only_For_The_Configured_Endpoint(string target, bool allowed)
    {
        var inner = new Capture();
        var egress = new HealingLlmEgressHandler(new HealingProviderOptions { BaseAddress = Base.ToString() })
        {
            InnerHandler = inner,
        };
        using var invoker = new HttpMessageInvoker(egress);
        using var request = new HttpRequestMessage(HttpMethod.Post, target);

        if (allowed)
        {
            (await invoker.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
            return;
        }

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        failure.Message.ShouldNotContain("evil");
        inner.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://127.0.0.1/", true)]
    [InlineData("https://169.254.169.254/", true)]
    [InlineData("https://10.1.2.3/", true)]
    [InlineData("https://172.20.0.1/", true)]
    [InlineData("https://192.168.1.1/", true)]
    [InlineData("https://[::1]/", true)]
    [InlineData("https://[fd00::1]/", true)]
    [InlineData("https://localhost/", true)]
    [InlineData("https://db/", true)]
    [InlineData("https://service.internal/", true)]
    [InlineData("https://api.provider.example/", false)]
    [InlineData("https://8.8.8.8/", false)]
    public void A_Base_Address_On_The_Platform_Network_Is_Refused_Outside_Development(string address, bool refused)
    {
        var options = new HealingProviderOptions
        {
            Provider = "http",
            BaseAddress = address,
            ApiKeySecret = SecretNames.HealingLlmApiKey,
        };

        var production = new HealingProviderOptionsValidator(new Env(Environments.Production)).Validate(null, options);
        var development = new HealingProviderOptionsValidator(new Env(Environments.Development)).Validate(null, options);

        production.Failed.ShouldBe(refused);
        if (refused)
        {
            (production.FailureMessage ?? string.Empty).ShouldContain(HealingProviderKeys.BaseAddress);
        }

        development.Failed.ShouldBeFalse();
    }

    private sealed class Capture : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":"{}","tokens":1,"cost":0.01}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Evidgate.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
