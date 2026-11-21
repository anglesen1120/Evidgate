using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>A provenance number token is bounded at 64 characters on the wire: the refusal keeps its code and pointer at any size.</summary>
public sealed partial class DraftTests
{
    [Theory]
    [InlineData(65)]
    [InlineData(204_800)]
    public async Task Create_ProvenanceNumberTokenOverTheBound_Is422OnProvenance(int digits)
    {
        await using var world = await StartAsync();
        var body = Body();
        // Finite (a run of 7s past the double range is refused earlier, by the idempotency canonicaliser), same raw length.
        body["provenance"] = JsonNode.Parse("{\"n\":0." + new string('0', digits - 3) + "7}")!;

        ShouldNameField(await CreateAsync(world, body), "provenance", $"{digits} digits");
        (await DraftCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ProvenanceNumberTokenAtTheBound_IsAccepted()
    {
        await using var world = await StartAsync();
        var body = Body();
        body["provenance"] = JsonNode.Parse("{\"n\":" + new string('7', 64) + "}")!;

        (await CreateAsync(world, body)).Status.ShouldBe(HttpStatusCode.Created);
    }
}
