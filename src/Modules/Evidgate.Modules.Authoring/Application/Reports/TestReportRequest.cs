using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>The bounds of one report: what a request may name and what a snapshot may hold.</summary>
internal static class TestReportLimits
{
    /// <summary>The task payload is capped at 64 KiB and carries the ids, so the id list is capped well under it.</summary>
    internal const int MaxSelectedIds = 1000;

    /// <summary>Sections a request may name.</summary>
    internal const int MaxSections = 4;

    /// <summary>Code points of the strategy and of the environment text.</summary>
    internal const int MaxTextCodePoints = 2000;

    /// <summary>Rows of one source kind (nodes, items, cases, requirements); more is a size outcome, never a cut.</summary>
    internal const int MaxRows = 5000;

    /// <summary>Requirement links of one report.</summary>
    internal const int MaxLinks = 20000;

    /// <summary>Canonical payload bytes; the same bound the table's check keeps.</summary>
    internal const int MaxPayloadBytes = 8 * 1024 * 1024;

    /// <summary>Stored bytes of every source row of one report, checked before the source is built into JSON.</summary>
    internal const int MaxSourceBytes = 16 * 1024 * 1024;

    /// <summary>The snapshot schema the payload is written in.</summary>
    internal const int SchemaVersion = 1;
}

/// <summary>The scope kinds a report can name and which of them have a source.</summary>
internal static class ReportScopeKinds
{
    internal const string FeatureNodes = "feature_nodes";
    internal const string Requirements = "requirements";
    internal const string ManualRun = "manual_run";
    internal const string Release = "release";

    /// <summary>True for every kind the contract names, built or not.</summary>
    internal static bool IsKnown(string kind) => kind is FeatureNodes or Requirements or ManualRun or Release;

    /// <summary>True for a kind whose source tables exist, so a snapshot can be built from it.</summary>
    internal static bool IsSupported(string kind) => kind is FeatureNodes or Requirements;
}

/// <summary>A validated create request.</summary>
/// <param name="ScopeKind">One of <see cref="ReportScopeKinds"/>.</param>
/// <param name="SelectedIds">Distinct ids, ascending, so the same selection always hashes the same.</param>
/// <param name="Sections">Distinct section ids in the order the report shows them.</param>
/// <param name="Language"><c>vi</c> or <c>en</c>.</param>
/// <param name="Strategy">Free text as written, or null.</param>
/// <param name="Environment">Free text as written, or null.</param>
internal sealed record ReportRequest(
    string ScopeKind,
    IReadOnlyList<Guid> SelectedIds,
    IReadOnlyList<string> Sections,
    string Language,
    string? Strategy,
    string? Environment);

/// <summary>Reads the create body member by member, so each mistake has its own 422 path.</summary>
internal static class TestReportBody
{
    /// <summary>The members of <c>POST …/authoring/reports</c>.</summary>
    internal static readonly string[] WriteMembers =
        ["scope_kind", "selected_ids", "sections", "language", "strategy", "environment"];

    /// <summary>Reads and validates a create body.</summary>
    /// <param name="body">The strict JSON object the route read.</param>
    internal static Result<ReportRequest> Read(JsonElement body)
    {
        if (Required(body, "scope_kind", JsonValueKind.String, out var kindElement) is { } kindError)
        {
            return Result.Failure<ReportRequest>(kindError);
        }

        var kind = kindElement.GetString()!;
        if (!ReportScopeKinds.IsKnown(kind))
        {
            return Result.Failure<ReportRequest>(AuthoringErrors.Invalid("/scope_kind", "invalid_value", "scope_kind is not a report scope"));
        }

        if (Required(body, "language", JsonValueKind.String, out var languageElement) is { } languageError)
        {
            return Result.Failure<ReportRequest>(languageError);
        }

        var language = languageElement.GetString()!;
        if (language is not ("vi" or "en"))
        {
            return Result.Failure<ReportRequest>(AuthoringErrors.Invalid("/language", "invalid_value", "language must be vi or en"));
        }

        var ids = Ids(body);
        if (!ids.IsSuccess)
        {
            return Result.Failure<ReportRequest>(ids.Error!);
        }

        var sections = Sections(body);
        if (!sections.IsSuccess)
        {
            return Result.Failure<ReportRequest>(sections.Error!);
        }

        if (Text(body, "strategy", out var strategy) is { } strategyError)
        {
            return Result.Failure<ReportRequest>(strategyError);
        }

        if (Text(body, "environment", out var environment) is { } environmentError)
        {
            return Result.Failure<ReportRequest>(environmentError);
        }

        return Result.Success(new ReportRequest(kind, ids.Value, sections.Value, language, strategy, environment));
    }

    private static Result<IReadOnlyList<Guid>> Ids(JsonElement body)
    {
        if (Required(body, "selected_ids", JsonValueKind.Array, out var array) is { } error)
        {
            return Result.Failure<IReadOnlyList<Guid>>(error);
        }

        if (array.GetArrayLength() is < 1 or > TestReportLimits.MaxSelectedIds)
        {
            return Result.Failure<IReadOnlyList<Guid>>(AuthoringErrors.Invalid(
                "/selected_ids", "out_of_range", "selected_ids must hold between 1 and 1000 ids"));
        }

        var ids = new SortedSet<Guid>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id) || id == Guid.Empty)
            {
                return Result.Failure<IReadOnlyList<Guid>>(AuthoringErrors.Invalid(
                    $"/selected_ids/{index}", "invalid_type", "each selected id must be a uuid"));
            }

            if (!ids.Add(id))
            {
                return Result.Failure<IReadOnlyList<Guid>>(AuthoringErrors.Invalid(
                    $"/selected_ids/{index}", "duplicate", "selected_ids must not repeat an id"));
            }

            index++;
        }

        return Result.Success<IReadOnlyList<Guid>>([.. ids]);
    }

    private static Result<IReadOnlyList<string>> Sections(JsonElement body)
    {
        if (Required(body, "sections", JsonValueKind.Array, out var array) is { } error)
        {
            return Result.Failure<IReadOnlyList<string>>(error);
        }

        if (array.GetArrayLength() is < 1 or > TestReportLimits.MaxSections)
        {
            return Result.Failure<IReadOnlyList<string>>(AuthoringErrors.Invalid(
                "/sections", "out_of_range", "sections must hold between 1 and 4 section ids"));
        }

        var sections = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var path = $"/sections/{sections.Count}";
            var id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (id is null || id.Length is < 1 or > 64)
            {
                return Result.Failure<IReadOnlyList<string>>(AuthoringErrors.Invalid(path, "invalid_type", "each section must be a 1..64 character id"));
            }

            if (sections.Contains(id, StringComparer.Ordinal))
            {
                return Result.Failure<IReadOnlyList<string>>(AuthoringErrors.Invalid(path, "duplicate", "sections must not repeat a section"));
            }

            sections.Add(id);
        }

        return Result.Success<IReadOnlyList<string>>(sections);
    }

    private static Error? Required(JsonElement body, string name, JsonValueKind kind, out JsonElement value)
    {
        if (!body.TryGetProperty(name, out value))
        {
            return AuthoringErrors.Invalid("/" + name, "required", name + " is required");
        }

        return value.ValueKind == kind ? null : AuthoringErrors.Invalid("/" + name, "invalid_type", name + " has the wrong type");
    }

    private static Error? Text(JsonElement body, string name, out string? value)
    {
        value = null;
        if (!body.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return AuthoringErrors.Invalid("/" + name, "invalid_type", name + " must be a string or null");
        }

        value = element.GetString()!;

        return value.EnumerateRunes().Count() > TestReportLimits.MaxTextCodePoints
            ? AuthoringErrors.Invalid("/" + name, "too_long", name + " is longer than 2000 characters")
            : null;
    }
}
