// 05.4.1-T1 §5: the optional, bounded, closed cancel body.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CancelRunEndpointTests
{
    /// <summary>
    /// §5: the body is optional — no body at all is "no reason", not a refusal, and the row records the absence
    /// as SQL NULL so a reader never has to tell an empty string from "none".
    /// </summary>
    [Fact]
    public async Task CancelRun_AbsentBody_AcceptedWithNoReason()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelAsync(world, runId, reason: null);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(_fixture, world, runId))
            .Reason.ShouldBeNull("absent means 'no reason', stored as NULL");
    }

    /// <summary>§5: an empty object and an explicit <c>null</c> member mean the same as an absent body.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reason":null}""")]
    public async Task CancelRun_NoReasonMember_AcceptedWithNoReason(string rawBody)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelRawAsync(world.Client, runId, rawBody, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        var row = await CancelRunTestData.RowAsync(_fixture, world, runId);
        row.Reason.ShouldBeNull();
        row.RequestedBy.ShouldBe(world.User);
    }

    /// <summary>
    /// §5 (recorded in <c>CancelRunRequest</c>): a blank reason is refused rather than stored, because "no reason"
    /// already has its own spelling — the refusal names the member, and nothing is recorded.
    /// </summary>
    [Fact]
    public async Task CancelRun_BlankReason_422InvalidValue()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, """{"reason":"   "}""", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        var detail = CancelRunTestData.Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/reason");
        detail.GetProperty("reason").GetString().ShouldBe("invalid_value");
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0);
    }

    /// <summary>§5: the reason is bounded at 1000 characters; one more is refused before any statement runs.</summary>
    [Fact]
    public async Task CancelRun_ReasonTooLong_422TooLong()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, $$"""{"reason":"{{new string('r', 1001)}}"}""", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = CancelRunTestData.Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/reason");
        detail.GetProperty("reason").GetString().ShouldBe("too_long");
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// §5 closed member set (00 §9.3): an unknown member is 422 <c>additional_properties</c> naming it, and a body
    /// that is not a JSON object is 422 <c>request_invalid</c> — a cancel never guesses what a caller meant.
    /// </summary>
    [Theory]
    [InlineData("""{"why":"because"}""", "/why", "additional_properties")]
    [InlineData("[]", "", "request_invalid")]
    [InlineData("not json", "", "request_invalid")]
    public async Task CancelRun_UnusableBody_422NothingRecorded(string rawBody, string path, string reason)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelRawAsync(world.Client, runId, rawBody, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = CancelRunTestData.Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// §5: the transport caps the body at 4 KiB before it is buffered whole — a 1000-character reason fits, a body
    /// that is over the edge answers 413 <c>payload_too_large</c> without parsing and records nothing.
    /// </summary>
    [Fact]
    public async Task CancelRun_BodyOverEdgeCap_413PayloadTooLarge()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, $$"""{"reason":"{{new string('r', 5000)}}"}""", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0);
    }
}
