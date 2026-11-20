using System.Text;
using System.Text.Json;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The guards the push mapper must keep that the rebuilt document used to enforce but a state machine can drop: one
/// root at all, a start record that names its element, one binding declared twice, a namespace value that is not XML,
/// a qualified name whose local part is not one, mapping after a fault, and the scope that decides which prefix an
/// attribute's namespace comes from. Each case carries the one-variable control that makes its refusal mean something.
/// </summary>
public sealed class PytestJUnitRecordMapperGuardTests
{
    private const string EndRecord = """{"t":"xml","e":"end"}""";

    private const string Rejected = """{"t":"rejected","code":"artifact_invalid","reason":"dtd_prohibited"}""";

    private static readonly JUnitMapContext Context = new(Guid.Empty, static (_, _) => null);

    [Fact]
    public void NoRoot_EmptyStreamOrTextOnly_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed();
        AssertCrashed(Text("x"));
    }

    [Fact]
    public void StartWithoutName_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed("""{"t":"xml","e":"start"}""", EndRecord);
    }

    [Fact]
    public void PrefixDeclaredTwiceOnOneElement_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["xmlns:p", "urn:a"], ["xmlns:q", "urn:b"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:p", "urn:a"], ["xmlns:p", "urn:b"]), EndRecord);
    }

    [Fact]
    public void NamespaceValueWithAnInvalidXmlChar_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["xmlns:p", "urn:p"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:p", "\u0001"]), EndRecord);
    }

    [Fact]
    public void QualifiedNameWithAnInvalidLocalPart_Deferred_Crashed()
    {
        // Both names are one prefix and an NCName, so only the local part can be what refuses them.
        AssertAccepts(Start("testsuite"), Start("child"), EndRecord, EndRecord);
        AssertCrashed(Start("testsuite"), Start("xml:bad name"), EndRecord, EndRecord);
        AssertAccepts(Start("testsuite", ["k", "v"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xml:bad name", "v"]), EndRecord);
    }

    [Fact]
    public void AcceptAfterFault_MapsNothing_AndStillSniffsTheChildRejection()
    {
        string[] records = [Start("testsuite"), Start("testcase", ["name", "one"]), EndRecord, EndRecord];

        var controlResolver = new CountingResolver();
        var control = Open(controlResolver);
        Feed(control, records);
        control.Finish().Cases.Count.ShouldBe(1);
        controlResolver.Calls.ShouldBe(1);

        // The unmatched end is the only variable: the fault it defers must stop the mapping, so the resolver is
        // never asked for a key the refused stream never earned.
        var faultResolver = new CountingResolver();
        var faulty = Open(faultResolver);
        Feed(faulty, [EndRecord, .. records, Rejected]);

        faultResolver.Calls.ShouldBe(0);
        faulty.RejectedReason.ShouldBe("dtd_prohibited");
        Should.Throw<JUnitParseRefusedException>(() => faulty.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    [Fact]
    public void NearestNamespaceScope_DecidesAttributeIdentity_Deferred_Crashed()
    {
        // The child rebinds `a` to the inner namespace, so its two attributes are the same expanded attribute even
        // though they were written with different prefixes.
        AssertCrashed(
            Start("testsuite", ["xmlns:a", "urn:outer"], ["xmlns:b", "urn:inner"]),
            Start("child", ["xmlns:a", "urn:inner"], ["a:n", "1"], ["b:n", "2"]),
            EndRecord,
            EndRecord);

        AssertAccepts(
            Start("testsuite", ["xmlns:a", "urn:outer"], ["xmlns:b", "urn:inner"]),
            Start("child", ["xmlns:a", "urn:other"], ["a:n", "1"], ["b:n", "2"]),
            EndRecord,
            EndRecord);
    }

    private static void AssertAccepts(params string[] records)
    {
        var sink = Open();
        Feed(sink, records);
        sink.Finish().Cases.ShouldBeEmpty();
    }

    private static void AssertCrashed(params string[] records)
    {
        var sink = Open();
        Feed(sink, records);
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    private static IJUnitRecordMapper Open(CountingResolver? resolver = null) =>
        new PytestJUnitMapper(new ParsingService()).OpenRecordMapper(
            resolver is null
                ? Context
                : new JUnitMapContext(Guid.Empty, resolver.Resolve));

    private static void Feed(IJUnitRecordMapper sink, params string[] records)
    {
        foreach (var record in records)
        {
            sink.Accept(Encoding.UTF8.GetBytes(record));
        }
    }

    private static string Text(string value) => JsonSerializer.Serialize(new { t = "xml", e = "text", v = value });

    private static string Start(string name, params string[][] attributes) =>
        JsonSerializer.Serialize(new { t = "xml", e = "start", n = name, a = attributes });

    /// <summary>A resolver that counts how often the mapper asks for a key, so a fault that stops mapping is visible.</summary>
    private sealed class CountingResolver
    {
        public int Calls { get; private set; }

        public string? Resolve(string classname, string name)
        {
            Calls++;

            return "sha256:" + new string('a', 64) + "::" + name;
        }
    }
}
