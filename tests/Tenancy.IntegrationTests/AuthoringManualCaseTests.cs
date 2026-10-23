using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Authoring.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T1.2: the manual test case <c>steps</c> document against
/// <c>contracts/schemas/authoring-steps.v1.json</c> through the module's own validator
/// (<see cref="ManualCaseStepsValidator"/>) — the path routes 13 and 15 will take, not a private copy
/// of the schema. No database: the validator is pure, and the parity corpus it shares with the CLI's
/// Ajv suite is the contract both runtimes must agree with.
/// </summary>
/// <remarks>
/// The bounds are the plan's (13.1.1 §3: 1..100 steps, every field at most 2 000 characters, unknown
/// members refused at every level); a 100-step document and a 2 000-character field are accepted and
/// one past either is not, so an off-by-one in the schema fails here rather than in a stubbed route.
/// </remarks>
public sealed partial class AuthoringManualCaseTests
{
    private const string CorpusDirectory = "fixtures/contracts/vectors/authoring-steps.v1";

    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly Lazy<ContractSchemaSet> Schemas =
        new(() => ContractSchemaSet.Load(Path.Combine(RepoRoot, "contracts")));

    private static readonly ManualCaseStepsValidator Validator = new(Schemas);

    /// <summary>Every corpus vector of the shared suite, so C# and TS cannot drift on this schema.</summary>
    public static IEnumerable<object[]> CorpusVectors()
    {
        var directory = Path.Combine(RepoRoot, CorpusDirectory);
        Directory.Exists(directory).ShouldBeTrue($"the parity corpus '{CorpusDirectory}' must exist");

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            if (file.EndsWith(".verdict.json", StringComparison.Ordinal))
            {
                continue;
            }

            yield return [Path.GetFileName(file)];
        }
    }

    [Fact]
    public void StepsSchema_RejectsUnknownMembers()
    {
        // The document is closed: a member the schema does not know is refused, not stored.
        Row(Validator.ValidateDocument(Element($$"""{"steps":{{Steps(1)}},"owner":"qa"}""")))
            .ShouldBe(["/owner|additional_properties"]);

        // A step is closed too: plan 13 §4.1 names no, action, expected and data — nothing else.
        Row(Validator.ValidateSteps(StepsElement(
                """[{"no":1,"action":"Open the page","expected":"The page is shown","note":"remember"}]""")))
            .ShouldBe(["/steps/0/note|additional_properties"]);

        // 'data' carries test data text, never a nested value (plan 13.1.1 §9).
        Row(Validator.ValidateSteps(StepsElement(
                """[{"no":1,"action":"Open","expected":"Shown","data":42}]""")))
            .ShouldBe(["/steps/0/data|type"]);

        // The step-count bounds: 0 and 101 are refused, 100 is accepted — both shapes (the request's
        // steps array and the stored {"steps":[…]} document) reach the same verdict.
        Row(Validator.ValidateSteps(StepsElement("[]"))).ShouldBe(["/steps|min_items"]);
        Row(Validator.ValidateDocument(Element($$"""{"steps":{{Steps(0)}}}"""))).ShouldBe(["/steps|min_items"]);
        Row(Validator.ValidateSteps(StepsElement(Steps(101)))).ShouldBe(["/steps|max_items"]);
        Validator.ValidateSteps(StepsElement(Steps(100))).IsValid.ShouldBeTrue("100 steps is the declared maximum");
        Validator.ValidateDocument(Element($$"""{"steps":{{Steps(100)}}}""")).IsValid.ShouldBeTrue();

        // The field bound is 2 000 characters, in the code-point unit the CHECK and the schema share:
        // 2 000 is accepted, 2 001 is one past it, for every text field of a step.
        var atLimit = Steps(1, action: new string('a', 2000), expected: new string('b', 2000), data: new string('c', 2000));
        Validator.ValidateSteps(StepsElement(atLimit)).IsValid.ShouldBeTrue("2 000 characters per field is the declared maximum");

        Row(Validator.ValidateSteps(StepsElement(Steps(1, action: new string('a', 2001)))))
            .ShouldBe(["/steps/0/action|max_length"]);
        Row(Validator.ValidateSteps(StepsElement(Steps(1, expected: new string('b', 2001)))))
            .ShouldBe(["/steps/0/expected|max_length"]);
        Row(Validator.ValidateSteps(StepsElement(Steps(1, data: new string('c', 2001)))))
            .ShouldBe(["/steps/0/data|max_length"]);

        // A step without a member plan 13 §4.1 requires is refused by name.
        Row(Validator.ValidateSteps(StepsElement("""[{"no":1,"expected":"The page is shown"}]""")))
            .ShouldBe(["/steps/0/action|required"]);
        Row(Validator.ValidateSteps(StepsElement("""[{"no":1,"action":"Open the page"}]""")))
            .ShouldBe(["/steps/0/expected|required"]);
        Row(Validator.ValidateSteps(StepsElement("""[{"action":"Open the page","expected":"Shown"}]""")))
            .ShouldBe(["/steps/0/no|required"]);

        // A step is an object, never a bare string, and a body without the member is one 'required'
        // row — the caller reports a detail row instead of inventing a second reason.
        Row(Validator.ValidateSteps(StepsElement("[1]"))).ShouldBe(["/steps/0|type"]);
        Row(Validator.ValidateSteps(null)).ShouldBe(["/steps|required"]);

        // No detail row echoes the step text (S3): a pointer and a reason, nothing else.
        Row(Validator.ValidateSteps(StepsElement(Steps(1, action: new string('z', 2001)))))
            .ShouldBe(["/steps/0/action|max_length"]);
    }

    /// <summary>
    /// The shared corpus (<c>fixtures/contracts/vectors/authoring-steps.v1</c>) through the module
    /// validator: the files the CLI's Ajv suite reads, with the verdicts both runtimes must reproduce.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusVectors))]
    public void StepsSchema_ParityCorpus(string vector)
    {
        var file = Path.Combine(RepoRoot, CorpusDirectory, vector);
        var sidecar = Element(File.ReadAllText(file[..^".json".Length] + ".verdict.json"));
        sidecar.GetProperty("schema").GetString().ShouldBe(ManualCaseStepsValidator.SchemaKey);

        var result = Validator.ValidateDocument(Element(File.ReadAllText(file)));
        var valid = sidecar.GetProperty("valid").GetBoolean();

        result.IsValid.ShouldBe(valid, vector);
        if (valid)
        {
            result.Errors.ShouldBeEmpty(vector);
            return;
        }

        var expected = sidecar.GetProperty("errors").EnumerateArray()
            .Select(e => $"{e.GetProperty("path").GetString()}|{e.GetProperty("reason").GetString()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Row(result).ShouldBe(expected, vector);
    }

    /// <summary>The <c>steps</c> value of a request body, the way routes 13/15 carry it.</summary>
    private static JsonElement? StepsElement(string stepsArrayJson) =>
        Element($$"""{"steps":{{stepsArrayJson}}}""").GetProperty("steps");

    /// <summary><paramref name="count"/> well-formed steps, optionally with one long field or a data value.</summary>
    private static string Steps(int count, string? action = null, string? expected = null, string? data = null)
    {
        var builder = new StringBuilder("[");
        for (var no = 1; no <= count; no++)
        {
            if (no > 1)
            {
                builder.Append(',');
            }

            builder.Append(
                CultureInfo.InvariantCulture,
                $$"""{"no":{{no}},"action":{{JsonSerializer.Serialize(action ?? "Open the page")}},"expected":{{JsonSerializer.Serialize(expected ?? "The page is shown")}}""");

            if (data is not null)
            {
                builder.Append(CultureInfo.InvariantCulture, $$""","data":{{JsonSerializer.Serialize(data)}}""");
            }

            builder.Append('}');
        }

        return builder.Append(']').ToString();
    }

    /// <summary>A detached element: the document is disposed, so the element outlives this helper.</summary>
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The verdict as <c>path|reason</c> rows, ordered the way both suites compare the corpus.</summary>
    private static string[] Row(ContractValidationResult result) =>
        result.Errors
            .Select(e => $"{e.Path}|{e.Reason}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
