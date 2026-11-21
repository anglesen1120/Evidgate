using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public class TelemetryMeterOrderTests
{
    private const string Part = TelemetryMeterRules.Directory + "EvidgateTelemetry.Quota.cs";
    private const string Holder = "internal static class EvidgateMeter { public static readonly Meter Instance = new(\"Evidgate\"); }";

    [Fact]
    public void Real_Telemetry_Partials_Read_The_Meter_Only_Through_The_Holder()
    {
        var files = SolutionLayout.ProductionSources();

        // Non-vacuity: the scan reaches the main file, the partials and the holder.
        files.Count(f => TelemetryMeterRules.IsTelemetryPartial(f.Path)).ShouldBeGreaterThanOrEqualTo(8);
        files.ShouldContain(f => f.Path == TelemetryMeterRules.HolderPath);

        var violations = TelemetryMeterRules.Find(files);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void A_Meter_Field_In_A_Partial_Is_A_Violation()
    {
        var seeded = new List<(string, string)>
        {
            (TelemetryMeterRules.HolderPath, Holder),
            (Part, "partial class EvidgateTelemetry { private static readonly Meter Meter = new(\"Evidgate\"); }"),
        };

        var violations = TelemetryMeterRules.Find(seeded);

        violations.ShouldContain(v => v.Contains("declares a Meter field", StringComparison.Ordinal));
        violations.ShouldContain(v => v.Contains("creates a Meter outside the holder", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Target_Typed_Meter_Creation_Outside_The_Holder_Is_A_Violation()
    {
        var seeded = new List<(string, string)>
        {
            (TelemetryMeterRules.HolderPath, Holder),
            (Part, "partial class EvidgateTelemetry { static void M() { Meter m = new(\"Evidgate\"); } }"),
        };

        TelemetryMeterRules.Find(seeded).ShouldHaveSingleItem().ShouldContain("creates a Meter outside the holder");
    }

    [Fact]
    public void A_Target_Typed_Non_Meter_Creation_Is_Not_A_Violation()
    {
        var seeded = new List<(string, string)>
        {
            (TelemetryMeterRules.HolderPath, Holder),
            (Part, "partial class EvidgateTelemetry { static readonly object O = new(); static readonly System.Text.StringBuilder S = new(); }"),
        };

        TelemetryMeterRules.Find(seeded).ShouldBeEmpty();
    }

    [Fact]
    public void An_Unqualified_Meter_Read_Is_A_Violation()
    {
        var seeded = new List<(string, string)>
        {
            (TelemetryMeterRules.HolderPath, Holder),
            (Part, "partial class EvidgateTelemetry { static readonly object C = Meter.CreateCounter<long>(\"x\"); }"),
        };

        TelemetryMeterRules.Find(seeded).ShouldHaveSingleItem().ShouldContain("reads an unqualified Meter");
    }

    [Fact]
    public void Holder_Access_And_A_Single_Holder_Meter_Is_Clean()
    {
        var seeded = new List<(string, string)>
        {
            (TelemetryMeterRules.HolderPath, Holder),
            (Part, "partial class EvidgateTelemetry { static readonly object C = EvidgateMeter.Instance.CreateCounter<long>(\"x\"); }"),
        };

        TelemetryMeterRules.Find(seeded).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(Holder + " " + Holder)]
    public void A_Holder_Without_Exactly_One_Meter_Is_A_Violation(string holderText)
    {
        TelemetryMeterRules.Find([(TelemetryMeterRules.HolderPath, holderText)])
            .ShouldHaveSingleItem().ShouldContain("exactly one Meter");
    }

    [Fact]
    public void Conditional_Text_In_A_Partial_Is_Refused()
    {
        var seeded = new List<(string, string)>
        {
            (TelemetryMeterRules.HolderPath, Holder),
            (Part, "partial class EvidgateTelemetry {\n#if DEBUG\n private static readonly Meter Meter = new(\"E\");\n#endif\n}"),
        };

        TelemetryMeterRules.Find(seeded).ShouldContain(v => v.Contains("conditional text", StringComparison.Ordinal));
    }
}
