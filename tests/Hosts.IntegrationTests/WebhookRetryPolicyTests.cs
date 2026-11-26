using Evidgate.Modules.Policy.Webhooks.Application;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed class WebhookRetryPolicyTests
{
    [Fact]
    public void Delay_DoublesWithJitter_AndNeverExceedsOneHour()
    {
        var start = DateTimeOffset.UnixEpoch;
        DateTimeOffset? previous = null;
        for (var attempt = 1; attempt <= 32; attempt++)
        {
            var next = WebhookRetryPolicy.Next(start, start, attempt, 0.5);
            next.ShouldNotBeNull();
            (next >= previous || previous is null).ShouldBeTrue();
            (next <= start.AddHours(1)).ShouldBeTrue();
            previous = next;
        }

        previous.ShouldBe(start.AddHours(1));
        WebhookRetryPolicy.Next(start, start, 2, 0.5).ShouldBe(start.AddMinutes(2));
        (WebhookRetryPolicy.Next(start, start, 2, 0.1) < WebhookRetryPolicy.Next(start, start, 2, 0.9)).ShouldBeTrue();
    }

    [Fact]
    public void Next_ClampsToDeadline_AndStopsAtTwentyFourHours()
    {
        var start = DateTimeOffset.UnixEpoch;
        WebhookRetryPolicy.Next(start, start.AddHours(24).AddTicks(-1), 8, 0).ShouldBe(start.AddDays(1));
        WebhookRetryPolicy.Next(start, start.AddHours(24), 8, 0).ShouldBeNull();
    }
}
