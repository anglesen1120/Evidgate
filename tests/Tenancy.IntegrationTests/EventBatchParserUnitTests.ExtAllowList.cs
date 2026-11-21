using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EventBatchParserUnitTests
{
    // ---- ext allow-list ---------------------------------------------------------------------

    [Fact]
    public void Unknown_Ext_Keys_Are_Dropped()
    {
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","evil":"x","also":1}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(SchemaExt);
    }

    [Fact]
    public void Adapter_Normalized_Status_Is_Dropped_While_The_Other_Status_Keys_Are_Kept()
    {
        var result = Parse(Batch(ValidEvent(
            type: "test.finished",
            status: "failed",
            ext: """{"schema":"playwright/1","normalized":"failed","skip_reason":"fixme","error_summary":"boom"}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(
            """{"schema":"playwright/1","skip_reason":"fixme","error_summary":"boom"}""");
    }

    [Fact]
    public void Capability_Carrier_Is_Not_Stored()
    {
        var result = Parse(Batch(ValidEvent(
            type: "run.log",
            ext: """{"schema":"playwright/1","capability":{"protocol":{"major":1}},"marker":"run_begin"}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe("""{"schema":"playwright/1","marker":"run_begin"}""");
        result.Events[0].Element.GetProperty("ext").GetProperty("capability").ValueKind
            .ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void Run_Log_Keys_Are_Not_Kept_On_A_Test_Envelope()
    {
        var result = Parse(Batch(ValidEvent(
            type: "test.started",
            ext: """{"schema":"playwright/1","marker":"run_begin","warn":{"code":"x"}}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(SchemaExt);
    }

    [Theory]
    [InlineData("test.started", false, SchemaExt)]
    [InlineData("test.finished", true, """{"schema":"playwright/1","xfail":true}""")]
    public void Xfail_Is_Read_Only_From_Test_Finished(string type, bool expected, string payload)
    {
        var result = Parse(Batch(ValidEvent(
            type: type,
            status: "failed",
            ext: """{"schema":"playwright/1","xfail":true}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].Xfail.ShouldBe(expected);
        result.Events[0].PayloadJson.ShouldBe(payload);
    }

    [Fact]
    public void Non_Boolean_Xfail_Does_Not_Set_The_Flag()
    {
        var result = Parse(Batch(ValidEvent(
            type: "test.finished",
            status: "passed",
            ext: """{"schema":"playwright/1","xfail":"yes"}""")));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].Xfail.ShouldBeFalse();
    }

    [Fact]
    public void Secret_Shaped_Key_Names_Are_Dropped_At_Any_Depth()
    {
        var result = Parse(Batch(ValidEvent(ext: """
            {"schema":"playwright/1","location":{"file":"a.ts","line":3,"token":"planted","api_key":"planted"},"authorization":"planted","password":"planted","project":"p"}
            """)));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe(
            """{"schema":"playwright/1","location":{"file":"a.ts","line":3},"project":"p"}""");
        result.Events[0].PayloadJson.ShouldNotContain("planted");
    }

    [Fact]
    public void An_Envelope_Without_Ext_Leaves_An_Empty_Residue()
    {
        var result = Parse(Batch(ValidEvent(ext: null)));

        result.IsAccepted.ShouldBeTrue();
        result.Events[0].PayloadJson.ShouldBe("{}");
    }

    [Fact]
    public void An_Ext_Residue_Under_The_Cap_Is_Kept()
    {
        var filler = new string('x', 8000);
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":{"file":""" + "\"" + filler + "\"}}")));

        result.IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public void An_Ext_Residue_Over_The_Cap_Is_Refused()
    {
        var filler = new string('x', 9000);
        var result = Parse(Batch(ValidEvent(ext: """{"schema":"playwright/1","location":{"file":""" + "\"" + filler + "\"}}")));

        result.IsAccepted.ShouldBeFalse();
        result.Events.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Path.ShouldBe("/events/0/ext");
        result.Errors[0].Reason.ShouldBe("ext_too_large");
        result.Errors[0].Index.ShouldBe(0);
    }
}
