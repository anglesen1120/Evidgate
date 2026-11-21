using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>Errors of the draft routes (00 §9.2).</summary>
internal static class DraftErrors
{
    internal static Error NotFound => Error.NotFound("no draft with that id is visible to this tenant");

    /// <summary>A caller whose principal carries no active membership of the tenant cannot author a draft.</summary>
    internal static Error AuthorRequired => new(
        ApiErrorCode.DraftAuthorRequired,
        "only an active member of this organization may author a draft",
        StatusCodes.Status403Forbidden);

    /// <summary>Content edits are legal only while the draft is still being edited; a PR-open draft is gone.</summary>
    internal static Error StateConflict(string status) => new(
        ApiErrorCode.DraftStateConflict,
        $"a {status} draft no longer accepts content edits",
        StatusCodes.Status409Conflict);

    /// <summary>The 422 a body that breaks the draft contract answers, with one detail per offending field.</summary>
    internal static Error Invalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.DraftInvalid, message, details);
}

/// <summary>
/// The body reading of a draft route. The endpoint hands the raw element over so an unauthorized caller is refused
/// before any member is inspected; everything below runs in the handler, after the pipeline's authorization stage.
/// </summary>
internal static class DraftBody
{
    internal const int MaxContentBytes = 262144;
    internal const int MaxProvenanceBytes = 4096;
    internal const int MaxFiles = 200;
    internal const int MaxPathLength = 512;
    internal const int MaxBodyRefLength = 512;
    internal const int MaxProvenanceMembers = 32;

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>A parsed create body: the typed request and the two JSON texts the row stores.</summary>
    /// <param name="Request">The validated request.</param>
    /// <param name="ContentJson">The content set as the row stores it.</param>
    /// <param name="ProvenanceJson">The provenance as the row stores it, or null.</param>
    internal sealed record CreateBody(CreateDraftRequest Request, string ContentJson, string? ProvenanceJson);

    /// <summary>The create body, or the 422 that names the first field it breaks.</summary>
    /// <param name="body">The raw request body.</param>
    internal static (CreateBody? Parsed, Error? Error) ReadCreate(JsonElement body)
    {
        var (request, readError) = Read<CreateDraftRequest>(body, "a draft");
        if (readError is not null || request is null)
        {
            return (null, readError);
        }

        var details = new List<ErrorDetail>();

        if (!DraftKind.All.Contains(request.Kind, StringComparer.Ordinal))
        {
            details.Add(new ErrorDetail("kind", "invalid_value"));
        }

        if (!IsLowerHex(request.BaseCommit, 7, 64))
        {
            details.Add(new ErrorDetail("base_commit", "invalid_value"));
        }

        var contentJson = Content(request.Content, details);
        var provenanceJson = Provenance(request.Provenance, details);

        return details.Count > 0
            ? (null, DraftErrors.Invalid("the draft body does not match the contract", details))
            : (new CreateBody(request, contentJson, provenanceJson), null);
    }

    /// <summary>The patch body (the replacement content set only), or the 422 naming the first member it breaks.</summary>
    /// <param name="body">The raw request body.</param>
    internal static (string? ContentJson, Error? Error) ReadUpdate(JsonElement body)
    {
        var (request, readError) = Read<UpdateDraftRequest>(body, "a draft");
        if (readError is not null || request is null)
        {
            return (null, readError);
        }

        var details = new List<ErrorDetail>();
        var contentJson = Content(request.Content, details);

        return details.Count > 0
            ? (null, DraftErrors.Invalid("the draft body does not match the contract", details))
            : (contentJson, null);
    }

    /// <summary>
    /// The typed body, refusing an unknown member anywhere so a misspelt field is a 422 instead of a silent default.
    /// The refusal names the member the serializer reported and never echoes its value.
    /// </summary>
    private static (T? Request, Error? Error) Read<T>(JsonElement body, string resource)
        where T : class
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return (null, DraftErrors.Invalid(
                "the body must be a JSON object",
                [new ErrorDetail(string.Empty, "invalid_type")]));
        }

        try
        {
            return (body.Deserialize<T>(Strict), null);
        }
        catch (JsonException exception)
        {
            var path = exception.Path?.TrimStart('$', '.') ?? string.Empty;
            return (null, DraftErrors.Invalid(
                $"the body does not match {resource}",
                [new ErrorDetail(path.Length == 0 ? string.Empty : path, "invalid")]));
        }
    }

    private static string Content(DraftContentDto content, List<ErrorDetail> details)
    {
        var files = content.Files;
        if (files is null || files.Count < 1 || files.Count > MaxFiles || files.Any(file => file is null))
        {
            details.Add(new ErrorDetail("content.files", "out_of_range"));
            return "{}";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"content.files[{index}]");

            if (!IsPath(file.Path))
            {
                details.Add(new ErrorDetail(prefix + ".path", "invalid_value"));
            }
            else if (!seen.Add(file.Path))
            {
                details.Add(new ErrorDetail(prefix + ".path", "duplicate"));
            }

            if (!IsSha256(file.BodySha))
            {
                details.Add(new ErrorDetail(prefix + ".body_sha", "invalid_value"));
            }

            if (!IsBodyRef(file.BodyRef))
            {
                details.Add(new ErrorDetail(prefix + ".body_ref", "invalid_value"));
            }
        }

        return JsonSerializer.Serialize(content, DraftJson.Options);
    }

    private static string? Provenance(JsonElement? provenance, List<ErrorDetail> details)
    {
        if (provenance is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            details.Add(new ErrorDetail("provenance", "invalid_type"));
            return null;
        }

        if (element.EnumerateObject().Count() > MaxProvenanceMembers)
        {
            details.Add(new ErrorDetail("provenance", "too_many"));
            return null;
        }

        var text = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaxProvenanceBytes)
        {
            details.Add(new ErrorDetail("provenance", "too_large"));
            return null;
        }

        return text;
    }

    /// <summary>The C# half of <c>draft_base_commit_check</c>: 7–64 lower-case hex characters.</summary>
    internal static bool IsLowerHex(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum && AllLowerHex(value.AsSpan());

    /// <summary><c>sha256:</c> followed by 64 lower-case hex characters.</summary>
    internal static bool IsSha256(string? value) =>
        value is not null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal)
        && AllLowerHex(value.AsSpan(7));

    private static bool AllLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    /// <summary>A repository-relative path: clean text, no leading slash, no backslash and no <c>..</c> segment.</summary>
    internal static bool IsPath(string? value) =>
        value is { Length: >= 1 and <= MaxPathLength }
        && TextRules.IsClean(value)
        && value[0] != '/'
        && !value.Contains('\\', StringComparison.Ordinal)
        && !value.Split('/').Any(segment => segment == "..");

    /// <summary>A body reference: clean text of 1–512 characters; it is a pointer, never the body.</summary>
    internal static bool IsBodyRef(string? value) =>
        value is { Length: >= 1 and <= MaxBodyRefLength } && TextRules.IsClean(value);
}

/// <summary>The caller's active membership of the tenant: the author of a new draft.</summary>
internal static class DraftActor
{
    /// <summary>The membership id, or null when the principal is not an active member of <paramref name="orgId"/>.</summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="principals">The caller.</param>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<Guid?> MembershipIdAsync(
        ITenantDb db,
        IRequestPrincipalAccessor principals,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        if (principals.Current is not { Kind: PrincipalKind.Member, UserId: { } userId })
        {
            return null;
        }

        return await db.ScalarAsync<Guid?>(
            DraftReadSql.ActorMembership,
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads a draft inside the caller's transaction, so the answer carries the version the write produced.</summary>
internal static class DraftReader
{
    /// <summary>The stored row, or null when no row of this tenant has the id.</summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<DraftRow?> RowAsync(ITenantDb db, Guid draftId, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<DraftRow>(DraftReadSql.DraftById, new { draft_id = draftId }, cancellationToken);

    /// <summary>The DTO of a row this transaction just wrote; the audit object names it.</summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="audit">The request's audit trail.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="status">201 for a create; 200 otherwise.</param>
    internal static async Task<Result<DraftDto>> SavedAsync(
        ITenantDb db,
        Guid draftId,
        IAuditTrail audit,
        CancellationToken cancellationToken,
        int status = 200)
    {
        var row = await RowAsync(db, draftId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"draft {draftId} was written in this transaction yet cannot be read back");

        audit.Describe(new AuditObject("draft", draftId.ToString("D")));

        return Result.Success(row.ToDto(), status);
    }
}
