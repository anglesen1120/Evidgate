using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Policy.Webhooks.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The delivery handler against a real HTTPS receiver: the schedule, the send-time address check, the redirect rule,
// the two time limits, the body cap, the signature and what never reaches last_error or a log.
public sealed partial class EvaluationTaskTests
{
    /// <summary>
    /// A receiver that always answers 500: every failure schedules the next attempt with a doubling, capped wait, and the
    /// attempt that falls on the 24-hour deadline dead-letters the row instead of scheduling a 25th hour.
    /// </summary>
    [Fact]
    public async Task WebhookRetry_Exponential_DeadLetters()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, _) = await SeedEndpointAsync(web, "/fail");
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);
        var delays = new List<TimeSpan>();

        for (var turn = 0; turn < 80; turn++)
        {
            var before = web.Clock.GetUtcNow();
            var deferral = await DeliverCatchingDeferralAsync(web, delivery);
            if (deferral is null)
            {
                break;
            }

            var pending = await ReadDeliveryAsync(web, delivery);
            pending.State.ShouldBe("retrying");
            pending.LastError.ShouldBe("http_error");
            pending.LastStatusCode.ShouldBe(500);
            delays.Add(pending.NextAttemptAt - before);
            deferral.Delay.ShouldBe(pending.NextAttemptAt - before, TimeSpan.FromMilliseconds(5), "the task waits for the stored time");
            web.Clock.SetUtcNow(pending.NextAttemptAt);
        }

        var final = await ReadDeliveryAsync(web, delivery);
        final.State.ShouldBe("dead_letter", "the window is spent");
        final.Attempts.ShouldBe(delays.Count + 1, "the dead-lettering attempt is the last one");
        delays.Count.ShouldBeGreaterThan(10);
        (web.Clock.GetUtcNow() - final.CreatedAt).ShouldBe(TimeSpan.FromHours(24), TimeSpan.FromMilliseconds(5));
        web.Receiver.HitsOn("/fail").ShouldBe(final.Attempts, "one POST per attempt, none after the dead letter");

        for (var i = 0; i < delays.Count; i++)
        {
            delays[i].ShouldBeLessThanOrEqualTo(WebhookRetryPolicy.MaxDelay + TimeSpan.FromMilliseconds(5));
            if (i < delays.Count - 1)
            {
                var expected = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, i), 60));
                delays[i].ShouldBeGreaterThanOrEqualTo(expected * 0.8 - TimeSpan.FromSeconds(1), $"attempt {i + 1} waits about 2^{i} minutes");
                delays[i].ShouldBeLessThanOrEqualTo(expected * 1.2 + TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>
    /// The host was public when the endpoint was registered and resolves to a private address at send time: the guard
    /// runs again for the attempt, nothing is dialed, and the row records the closed code and waits for a later try.
    /// </summary>
    [Fact]
    public async Task Delivery_DnsRebinding_BlockedAtSendTime()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, _) = await SeedEndpointAsync(web);
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);

        web.Resolver.Set(WebhookTestReceiver.Host, FakeWebhookHostResolver.PrivateAddress);
        var deferral = await DeliverCatchingDeferralAsync(web, delivery);

        deferral.ShouldNotBeNull("a blocked attempt is retried, not dropped");
        var row = await ReadDeliveryAsync(web, delivery);
        row.State.ShouldBe("retrying");
        row.LastError.ShouldBe("dns_rebinding_blocked");
        row.LastStatusCode.ShouldBeNull();
        web.Receiver.Requests.ShouldBeEmpty("the receiver is never contacted");
        web.Connector.Dialed.ShouldBeEmpty("no socket is opened for a blocked address");
    }

    /// <summary>A 302 is a failed attempt: the Location is never requested, on this host or another.</summary>
    [Fact]
    public async Task Delivery_RedirectNotFollowed()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, _) = await SeedEndpointAsync(web, "/redirect");
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);

        (await DeliverCatchingDeferralAsync(web, delivery)).ShouldNotBeNull();

        var row = await ReadDeliveryAsync(web, delivery);
        row.State.ShouldBe("retrying");
        row.LastError.ShouldBe("redirect_not_followed");
        row.LastStatusCode.ShouldBe(302);
        web.Receiver.HitsOn("/redirect").ShouldBe(1);
        web.Receiver.HitsOn("/landing").ShouldBe(0, "the second endpoint records no hit");
        web.Connector.Dialed.Count.ShouldBe(1, "one connection, to the validated address only");
    }

    /// <summary>A receiver that stalls is cut off at the total limit and the attempt is recorded as a timeout.</summary>
    [Fact]
    public async Task Delivery_TimeoutEnforced()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Webhooks:Delivery:ConnectTimeout"] = "00:00:00.500",
            ["Webhooks:Delivery:TotalTimeout"] = "00:00:01",
        };
        await using var web = await StartWebhookWorldAsync(configuration);
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, _) = await SeedEndpointAsync(web, "/slow");
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);

        var watch = Stopwatch.StartNew();
        var deferral = await DeliverCatchingDeferralAsync(web, delivery);
        watch.Stop();

        deferral.ShouldNotBeNull();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6), "the receiver stalls for 8 s; the limit is 1 s");
        var row = await ReadDeliveryAsync(web, delivery);
        row.State.ShouldBe("retrying");
        row.LastError.ShouldBe("timeout");
        row.LastStatusCode.ShouldBeNull();
    }

    /// <summary>The reader stops at 4 KiB without waiting for the chunked reply to end.</summary>
    [Fact]
    public async Task Delivery_ResponseBodyCapped()
    {
        var reads = new WebhookResponseReadCounter();
        await using var web = await StartWebhookWorldAsync(responseObserver: reads);
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, _) = await SeedEndpointAsync(web, "/big");
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);

        await DeliverAsync(web, delivery);

        var row = await ReadDeliveryAsync(web, delivery);
        row.State.ShouldBe("delivered");
        row.LastStatusCode.ShouldBe(200);
        reads.BytesRead.ShouldBe(4096);
        await web.Receiver.BigDisconnected.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The receiver's verification rule: HMAC-SHA256 keyed with the whsec_ text over <c>timestamp.body</c>, with the
    /// timestamp taken from the header; the event headers name the evaluation and the type, and the body names the same
    /// evaluation. A signature over the body alone would not verify.
    /// </summary>
    [Fact]
    public async Task Delivery_SignatureHeaders_VerifyWithReceiverRule()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, secret) = await SeedEndpointAsync(web);
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);

        await DeliverAsync(web, delivery);

        var received = web.Receiver.Requests.Single();
        var headers = received.Headers;
        headers[WebhookSigner.EventIdHeader].ShouldBe(evaluation.ToString("D"));
        headers[WebhookSigner.EventTypeHeader].ShouldBe("evaluation.created");
        var timestamp = long.Parse(headers[WebhookSigner.TimestampHeader], System.Globalization.CultureInfo.InvariantCulture);
        timestamp.ShouldBe(web.Clock.GetUtcNow().ToUnixTimeSeconds());

        var key = Encoding.UTF8.GetBytes(secret);
        var signed = Encoding.ASCII.GetBytes($"{timestamp}.").Concat(received.Body).ToArray();
        headers[WebhookSigner.SignatureHeader].ShouldBe("v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, signed)));
        headers[WebhookSigner.SignatureHeader]
            .ShouldNotBe("v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, received.Body)), "the timestamp is part of what is signed");

        using var body = JsonDocument.Parse(received.Body);
        body.RootElement.GetProperty("evaluation_id").GetGuid().ShouldBe(evaluation);
    }

    /// <summary>A failing attempt and an unreadable secret leave no secret, signature or response text in last_error or any log.</summary>
    [Fact]
    public async Task Delivery_SecretNeverInLastErrorOrLogs()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (failing, secret) = await SeedEndpointAsync(web, "/fail");
        var (broken, _) = await SeedEndpointAsync(web, unreadableSecret: true);
        var failingDelivery = await SeedDeliveryAsync(web, failing, evaluation);
        var brokenDelivery = await SeedDeliveryAsync(web, broken, evaluation);

        (await DeliverCatchingDeferralAsync(web, failingDelivery)).ShouldNotBeNull();
        (await DeliverCatchingDeferralAsync(web, brokenDelivery)).ShouldNotBeNull();

        var failedRow = await ReadDeliveryAsync(web, failingDelivery);
        var brokenRow = await ReadDeliveryAsync(web, brokenDelivery);
        brokenRow.LastError.ShouldBe("secret_unreadable");
        var signature = web.Receiver.Requests.Single().Headers[WebhookSigner.SignatureHeader];
        var stored = new[] { failedRow.LastError, brokenRow.LastError };
        var logged = web.Logs.Entries.SelectMany(entry => entry.Texts).ToList();

        web.Logs.Entries.ShouldContain(
            entry => entry.Message.Contains(failingDelivery.ToString("D"), StringComparison.Ordinal),
            "the capture is live, so the absence below means something");
        foreach (var text in stored.Concat(logged))
        {
            text.ShouldNotBeNull();
            text.ShouldNotContain(secret);
            text.ShouldNotContain("whsec_");
            text.ShouldNotContain(signature);
            text.ShouldNotContain("upstream exploded", Case.Sensitive, "the response text is never kept");
        }
    }

    /// <summary>An endpoint disabled or deleted after the row was queued gets nothing: the row dead-letters and stays there.</summary>
    [Fact]
    public async Task Delivery_DisabledEndpoint_NotSent()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (disabled, _) = await SeedEndpointAsync(web);
        var (deleted, _) = await SeedEndpointAsync(web);
        var disabledDelivery = await SeedDeliveryAsync(web, disabled, evaluation);
        var deletedDelivery = await SeedDeliveryAsync(web, deleted, evaluation);
        await SetEndpointStateAsync(web, disabled, "disabled");
        await SetEndpointStateAsync(web, deleted, "deleted");

        foreach (var delivery in new[] { disabledDelivery, deletedDelivery })
        {
            await DeliverAsync(web, delivery);
            await DeliverAsync(web, delivery);

            var row = await ReadDeliveryAsync(web, delivery);
            row.State.ShouldBe("dead_letter");
            row.LastError.ShouldBe("endpoint_disabled");
            row.Attempts.ShouldBe(1, "a duplicate task finds a terminal row and does nothing");
        }

        web.Receiver.Requests.ShouldBeEmpty();
        web.Connector.Dialed.ShouldBeEmpty();
    }

    /// <summary>
    /// The first success pins the connection to the validated address, and a duplicate task for the delivered row sends
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Delivery_Success_DialsValidatedAddressOnceAndIgnoresDuplicateTask()
    {
        await using var web = await StartWebhookWorldAsync();
        var evaluation = await SeedEvaluationAsync(web);
        var (endpoint, _) = await SeedEndpointAsync(web);
        var delivery = await SeedDeliveryAsync(web, endpoint, evaluation);

        await DeliverAsync(web, delivery);
        await DeliverAsync(web, delivery);

        var row = await ReadDeliveryAsync(web, delivery);
        row.State.ShouldBe("delivered");
        row.Attempts.ShouldBe(1);
        row.LastError.ShouldBeNull();
        web.Receiver.Requests.Count.ShouldBe(1, "a delivered row is never sent again");
        web.Connector.Dialed.Single().ShouldBe([FakeWebhookHostResolver.PublicAddress]);
    }
}
