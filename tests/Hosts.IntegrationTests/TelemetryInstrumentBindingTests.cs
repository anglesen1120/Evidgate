using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Evidgate.BuildingBlocks.Observability;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// Every instrument of <see cref="EvidgateTelemetry"/> is created from the one holder meter, whatever order the
/// compiler sees the partial files in, and the dump of what they publish is the catalogue's identity.
/// </summary>
public sealed class TelemetryInstrumentBindingTests(ITestOutputHelper output)
{
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void Every_Static_Instrument_Is_Non_Null_And_Bound_To_The_Holder_Meter()
    {
        var holder = HolderMeter();
        var instruments = Instruments();

        instruments.Count.ShouldBe(33);
        foreach (var (field, instrument) in instruments)
        {
            instrument.ShouldNotBeNull($"{field} is null: its initialiser ran before the meter existed");
            ReferenceEquals(instrument.Meter, holder).ShouldBeTrue($"{field} is bound to a different Meter instance");
        }
    }

    [Fact]
    public void Holder_Meter_Carries_The_Public_Name_And_No_Version()
    {
        var holder = HolderMeter();

        holder.Name.ShouldBe(EvidgateTelemetry.MeterName);
        holder.Version.ShouldBeNull();
    }

    [Fact]
    public void Instrument_Dump_Of_Name_Unit_Description_Meter()
    {
        var lines = Instruments()
            .Select(i => $"{i.Instrument.Name}|{i.Instrument.Unit}|{i.Instrument.Description}|{i.Instrument.Meter.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        lines.Count.ShouldBe(33);
        output.WriteLine("INSTRUMENT-DUMP-BEGIN");
        lines.ForEach(output.WriteLine);
        output.WriteLine("INSTRUMENT-DUMP-END");
    }

    private static List<(string Field, Instrument Instrument)> Instruments()
    {
        RuntimeHelpers.RunClassConstructor(typeof(EvidgateTelemetry).TypeHandle);
        return typeof(EvidgateTelemetry).GetFields(StaticFields)
            .Where(f => typeof(Instrument).IsAssignableFrom(f.FieldType) && !f.IsLiteral)
            .Select(f => (f.Name, (Instrument)f.GetValue(null)!))
            .ToList();
    }

    private static Meter HolderMeter()
    {
        var holderType = typeof(EvidgateTelemetry).Assembly.GetType("Evidgate.BuildingBlocks.Observability.EvidgateMeter", throwOnError: true)!;
        return (Meter)holderType.GetField("Instance", StaticFields)!.GetValue(null)!;
    }
}
