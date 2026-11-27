using System.Text;
using System.Text.Json;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The gateway composed with the module's own RedactionPipeline, not a double: what the provider receives and what a
// refusal leaves behind are the pipeline's real output.
public sealed partial class HealingLlmGatewayTests
{
    [Fact]
    public async Task InvokeHealingLlm_RealPipelineSensitiveBundle_ProviderReceivesNoSensitiveBytes()
    {
        // Built at run time: stand-ins for a bearer token, a password and an address, never literals in the repository.
        var bearer = string.Concat("Q7w8E9r0T1y2U3i4", "O5p6A7s8D9f0G1h2");
        var password = string.Concat("hunter2", "hunter2");
        var email = string.Concat("gateway.person", "@", "example.test");
        var payload = string.Concat(
            "Authorization: Bearer ", bearer, "\npassword=", password, "\nContact: ", email, "\nstep: open the cart");
        var received = new List<string>();
        var provider = new StubProvider(request =>
        {
            received.Add(request.Payload);
            return new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m);
        });
        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project) with { Payload = payload });

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Ok);
        provider.Calls.ShouldBe(1);
        var sent = received.ShouldHaveSingleItem();
        sent.ShouldContain("step: open the cart", Case.Sensitive, "the control: the rest of the payload did reach the provider");
        sent.ShouldContain("[redacted:", Case.Sensitive);
        foreach (var sensitive in new[] { bearer, password, email })
        {
            ContainsBytes(sent, sensitive).ShouldBeFalse("a sensitive value must not reach the provider");
        }

        (await ReadLeakAuditsAsync(world)).ShouldBeEmpty();
    }

    private static bool ContainsBytes(string text, string value) =>
        Encoding.UTF8.GetBytes(text).AsSpan().IndexOf(Encoding.UTF8.GetBytes(value).AsSpan()) >= 0;

    [Fact]
    public async Task InvokeHealingLlm_RealPipelineScanLimitExceeded_SendsNothingAuditsOnceAndWarns()
    {
        // 4,097 base64 tokens that each decode to harmless text: one more than a 64 KiB window may decode, so the
        // pipeline cannot vouch for the window and refuses it. The input itself holds nothing secret.
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));
        var payload = string.Join(' ', Enumerable.Repeat(token, 4097));
        payload.Length.ShouldBeLessThan(RedactionPipeline.MaxWindowChars, "one window, so only the decode bound can refuse it");
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        var logger = new CapturingLogger();
        await using var world = await StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider));
            services.AddSingleton<ILogger<HealingLlmGateway>>(logger);
        });

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project) with { Payload = payload });

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.BlockedLeak);
        provider.Calls.ShouldBe(0, "a refused payload must never reach the provider");
        var audit = (await ReadLeakAuditsAsync(world)).ShouldHaveSingleItem();
        audit.ObjectId.ShouldBe(invocation.CallId.ToString("D"));
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(audit.Metadata).ShouldNotBeNull();
        metadata["rule_id"].ShouldBe("residual_scan_truncated");
        metadata["segment_ordinal"].ShouldBe("0");

        var warning = logger.Events.Where(entry => entry.Id.Id == 8612).ShouldHaveSingleItem();
        warning.Id.Name.ShouldBe("LogLeakBlocked");
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(world.Project.ToString("D"), Case.Sensitive);
        warning.Message.ShouldNotContain(token, Case.Sensitive);
    }
}
