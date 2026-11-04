using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>One priority level's guidance, in the four levels a checklist item can carry.</summary>
/// <param name="Critical">What a critical item must cover.</param>
/// <param name="High">What a high item must cover.</param>
/// <param name="Medium">What a medium item must cover.</param>
/// <param name="Low">What a low item must cover.</param>
internal sealed record AiSettingsPriorityCriteria(string Critical, string High, string Medium, string Low);

/// <summary>
/// The immutable settings document of a project: the eight fields a generation reads, in the composed and
/// trimmed form they are stored and hashed under. The canonical form — and therefore <see cref="ContentHash"/>
/// — depends on exactly these members, never on the order the caller sent them in.
/// </summary>
internal sealed record AiSettingsDocument(
    string Description,
    string? EnvironmentUrl,
    IReadOnlyList<string> Languages,
    string WritingStyle,
    string CoverageStrategy,
    AiSettingsPriorityCriteria PriorityCriteria,
    string ExclusionRules,
    string StepDetail)
{
    /// <summary>The most details one refusal carries, so a hostile body cannot inflate the envelope.</summary>
    internal const int MaxDetails = 50;

    /// <summary>The document's members, in the order its details are reported.</summary>
    internal static readonly string[] Members =
    [
        "description",
        "environment_url",
        "languages",
        "writing_style",
        "coverage_strategy",
        "priority_criteria",
        "exclusion_rules",
        "step_detail",
    ];

    /// <summary>The four members of <c>priority_criteria</c>, in reporting order.</summary>
    private static readonly string[] CriteriaMembers = ["critical", "high", "medium", "low"];

    /// <summary>
    /// Reads and validates the <c>settings</c> member of a save. Every rule of the story is applied here, in
    /// field order, and a refusal names the JSON pointer and one of the story's reason tokens.
    /// </summary>
    /// <param name="settings">The request's <c>settings</c> element.</param>
    /// <param name="rootPath">The pointer of the member itself, <c>/settings</c>.</param>
    /// <returns>The document, or the details that refuse it.</returns>
    internal static (AiSettingsDocument? Document, IReadOnlyList<ErrorDetail> Details) Parse(JsonElement settings, string rootPath = "/settings")
    {
        var details = new Details();

        if (settings.ValueKind != JsonValueKind.Object)
        {
            details.Add(rootPath, "invalid");

            return (null, details.ToList());
        }

        foreach (var member in settings.EnumerateObject())
        {
            if (!Members.Contains(member.Name, StringComparer.Ordinal))
            {
                details.Add(rootPath + "/" + AuthoringText.Brief(member.Name), "unknown_field");
            }
        }

        var description = ReadText(settings, "description", rootPath, AiSettingsText.MaxDescriptionLength, details);
        var environmentUrl = ReadUrl(settings, rootPath, details);
        var languages = ReadLanguages(settings, rootPath, details);
        var writingStyle = ReadText(settings, "writing_style", rootPath, AiSettingsText.MaxWritingStyleLength, details);
        var coverageStrategy = ReadText(settings, "coverage_strategy", rootPath, AiSettingsText.MaxCoverageStrategyLength, details);
        var criteria = ReadCriteria(settings, rootPath, details);
        var exclusionRules = ReadText(settings, "exclusion_rules", rootPath, AiSettingsText.MaxExclusionRulesLength, details);
        var stepDetail = ReadText(settings, "step_detail", rootPath, AiSettingsText.MaxStepDetailLength, details);

        if (details.Count > 0 || description is null || languages is null || writingStyle is null
            || coverageStrategy is null || criteria is null || exclusionRules is null || stepDetail is null)
        {
            return (null, details.ToList());
        }

        return (new AiSettingsDocument(
            description,
            environmentUrl,
            languages,
            writingStyle,
            coverageStrategy,
            criteria,
            exclusionRules,
            stepDetail), []);
    }

    /// <summary>
    /// Reads a stored template document: the same eight members, already in canonical form. A template is
    /// trusted migration data, so a shape it cannot carry is a server fault rather than a caller refusal.
    /// </summary>
    /// <param name="content">The template's <c>content</c> object.</param>
    /// <returns>The document.</returns>
    internal static AiSettingsDocument FromStored(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("a stored ai settings template is not a JSON object");
        }

        var criteria = StoredObject(content, "priority_criteria");

        return new AiSettingsDocument(
            StoredString(content, "description"),
            content.TryGetProperty("environment_url", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null,
            StoredLanguages(content),
            StoredString(content, "writing_style"),
            StoredString(content, "coverage_strategy"),
            new AiSettingsPriorityCriteria(
                StoredString(criteria, "critical"),
                StoredString(criteria, "high"),
                StoredString(criteria, "medium"),
                StoredString(criteria, "low")),
            StoredString(content, "exclusion_rules"),
            StoredString(content, "step_detail"));
    }

    /// <summary>
    /// The RFC 8785 digest of the eight members: what a version is addressed by, and the component a later
    /// generation cache key and provenance pin carry.
    /// </summary>
    /// <returns><c>sha256:&lt;lowercase hex&gt;</c>.</returns>
    internal string ContentHash() => CanonicalHash.Compute(CanonicalJson());

    /// <summary>The settings as the stored wire document, in the member order of <see cref="Members"/>.</summary>
    /// <returns>A JSON object with the eight members.</returns>
    internal JsonObject ToJson() => new()
    {
        ["description"] = Description,
        ["environment_url"] = EnvironmentUrl,
        ["languages"] = new JsonArray([.. Languages.Select(language => JsonValue.Create(language))]),
        ["writing_style"] = WritingStyle,
        ["coverage_strategy"] = CoverageStrategy,
        ["priority_criteria"] = new JsonObject
        {
            ["critical"] = PriorityCriteria.Critical,
            ["high"] = PriorityCriteria.High,
            ["medium"] = PriorityCriteria.Medium,
            ["low"] = PriorityCriteria.Low,
        },
        ["exclusion_rules"] = ExclusionRules,
        ["step_detail"] = StepDetail,
    };

    /// <summary>The minimal JSON text that is hashed.</summary>
    private string CanonicalJson() => ToJson().ToJsonString();

    private static string? ReadText(JsonElement settings, string name, string rootPath, int max, Details details)
    {
        if (!settings.TryGetProperty(name, out var member))
        {
            details.Add(rootPath + "/" + name, "required");

            return null;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            details.Add(rootPath + "/" + name, "invalid");

            return null;
        }

        var text = AiSettingsText.Prepare(member.GetString());

        if (AiSettingsText.CountCodePoints(text) > max)
        {
            details.Add(rootPath + "/" + name, "too_long");

            return null;
        }

        if (SecretShape.ValueLooksSecret(text))
        {
            details.Add(rootPath + "/" + name, "secret_value_detected");

            return null;
        }

        return text;
    }

    private static string? ReadUrl(JsonElement settings, string rootPath, Details details)
    {
        if (!settings.TryGetProperty("environment_url", out var member))
        {
            details.Add(rootPath + "/environment_url", "required");

            return null;
        }

        if (member.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            details.Add(rootPath + "/environment_url", "invalid");

            return null;
        }

        var url = AiSettingsText.Prepare(member.GetString());

        if (AiSettingsText.CountCodePoints(url) > AiSettingsText.MaxEnvironmentUrlLength)
        {
            details.Add(rootPath + "/environment_url", "too_long");

            return null;
        }

        var problem = UrlSafety.Problem("environment_url", url);
        if (problem is not null)
        {
            details.Add(rootPath + "/environment_url", problem);

            return null;
        }

        return url;
    }

    private static string[]? ReadLanguages(JsonElement settings, string rootPath, Details details)
    {
        if (!settings.TryGetProperty("languages", out var member))
        {
            details.Add(rootPath + "/languages", "required");

            return null;
        }

        if (member.ValueKind != JsonValueKind.Array)
        {
            details.Add(rootPath + "/languages", "invalid");

            return null;
        }

        var languages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failed = false;

        foreach (var item in member.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                details.Add(rootPath + "/languages", "invalid");
                failed = true;
                continue;
            }

            var language = AiSettingsText.Prepare(item.GetString());

            if (!seen.Add(language))
            {
                details.Add(rootPath + "/languages", "duplicate_value");
                failed = true;
            }
            else if (!AiSettingsText.IsSupportedLanguage(language))
            {
                details.Add(rootPath + "/languages", "not_allowed_value");
                failed = true;
            }
            else
            {
                languages.Add(language);
            }
        }

        if (languages.Count == 0 && !failed)
        {
            details.Add(rootPath + "/languages", "required");

            return null;
        }

        return failed ? null : AiSettingsText.CanonicalLanguages(languages);
    }

    private static AiSettingsPriorityCriteria? ReadCriteria(JsonElement settings, string rootPath, Details details)
    {
        var path = rootPath + "/priority_criteria";

        if (!settings.TryGetProperty("priority_criteria", out var member))
        {
            details.Add(path, "required");

            return null;
        }

        if (member.ValueKind != JsonValueKind.Object)
        {
            details.Add(path, "invalid");

            return null;
        }

        foreach (var extra in member.EnumerateObject())
        {
            if (!CriteriaMembers.Contains(extra.Name, StringComparer.Ordinal))
            {
                details.Add(path + "/" + AuthoringText.Brief(extra.Name), "unknown_field");
            }
        }

        var values = new string?[CriteriaMembers.Length];

        for (var index = 0; index < CriteriaMembers.Length; index++)
        {
            values[index] = ReadText(member, CriteriaMembers[index], path, AiSettingsText.MaxPriorityCriterionLength, details);
        }

        if (values.Any(value => value is null))
        {
            return null;
        }

        return new AiSettingsPriorityCriteria(values[0]!, values[1]!, values[2]!, values[3]!);
    }

    private static string StoredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String
            ? member.GetString()!
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"a stored ai settings template has no string member '{name}'"));

    private static JsonElement StoredObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Object
            ? member
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"a stored ai settings template has no object member '{name}'"));

    private static string[] StoredLanguages(JsonElement element)
    {
        if (!element.TryGetProperty("languages", out var member) || member.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("a stored ai settings template has no languages array");
        }

        var languages = new List<string>();
        foreach (var item in member.EnumerateArray())
        {
            languages.Add(item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidOperationException("a stored ai settings template has a non-string language"));
        }

        return [.. languages];
    }

    /// <summary>The bounded, ordered detail collector of one validation pass.</summary>
    private sealed class Details
    {
        private readonly List<ErrorDetail> _details = [];

        internal int Count => _details.Count;

        internal void Add(string path, string reason)
        {
            if (_details.Count < MaxDetails)
            {
                _details.Add(new ErrorDetail(path, reason));
            }
        }

        internal List<ErrorDetail> ToList() => _details;
    }
}
