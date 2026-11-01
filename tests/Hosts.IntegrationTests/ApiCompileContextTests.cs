using System.Text.Json;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The contract schema set compiles its validators with Roslyn the first time a host validates a body, and that compile
/// resolves the reference assemblies listed in the host's <c>deps.json</c>. A build without the compile context carries
/// none of them, so the first schema-validated request answers 500; this test reads the built Api's own file and fails
/// before that reaches a running host.
/// </summary>
public sealed class ApiCompileContextTests
{
    [Fact]
    public void Api_DepsJson_CarriesTheCompileContextTheContractCompileNeeds()
    {
        var assembly = typeof(Evidgate.Api.ApiHost).Assembly;
        var deps = Path.ChangeExtension(assembly.Location, ".deps.json");
        File.Exists(deps).ShouldBeTrue($"the Api deps file is not next to the built assembly: {deps}");

        using var document = JsonDocument.Parse(File.ReadAllText(deps));
        var target = document.RootElement.GetProperty("targets").EnumerateObject().Single().Value;

        // The runtime pack's reference assemblies are the compile assets Roslyn resolves System.* from; without
        // PreserveCompilationContext no library in the file has a compile section at all.
        var runtime = target.EnumerateObject()
            .Where(library => library.Name.StartsWith("System.Runtime/", StringComparison.Ordinal))
            .Select(library => library.Value)
            .FirstOrDefault(value => value.TryGetProperty("compile", out _));

        runtime.ValueKind.ShouldBe(JsonValueKind.Object, "the Api deps file lists no compile assets; set PreserveCompilationContext on src/Evidgate.Api");
        runtime.TryGetProperty("compile", out var compile).ShouldBeTrue();
        compile.TryGetProperty("System.Runtime.dll", out _).ShouldBeTrue(
            "the runtime reference assemblies are missing from the Api deps file, so a runtime validator compile cannot resolve System.*");
    }
}
