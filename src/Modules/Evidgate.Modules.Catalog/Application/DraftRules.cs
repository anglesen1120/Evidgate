using System.Globalization;
using System.Runtime.InteropServices;
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

    /// <summary>Longest provenance number token, in characters.</summary>
    private const int MaxNumberTokenLength = 64;

    /// <summary>Largest exponent magnitude a provenance number may carry.</summary>
    private const int MaxNumberExponent = 400;

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
    /// <param name="db">The tenant-scoped seam of the request transaction, used only to measure both JSON columns.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<(CreateBody? Parsed, Error? Error)> ReadCreateAsync(
        ITenantDb db,
        JsonElement body,
        CancellationToken cancellationToken)
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

        if (details.Count == 0)
        {
            await MeasureAsync(db, contentJson, provenanceJson, details, cancellationToken).ConfigureAwait(false);
        }

        return details.Count > 0
            ? (null, DraftErrors.Invalid("the draft body does not match the contract", details))
            : (new CreateBody(request, contentJson, provenanceJson), null);
    }

    /// <summary>The patch body (the replacement content set only), or the 422 naming the first member it breaks.</summary>
    /// <param name="db">The tenant-scoped seam of the request transaction, used only to measure the content column.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<(string? ContentJson, Error? Error)> ReadUpdateAsync(
        ITenantDb db,
        JsonElement body,
        CancellationToken cancellationToken)
    {
        var (request, readError) = Read<UpdateDraftRequest>(body, "a draft");
        if (readError is not null || request is null)
        {
            return (null, readError);
        }

        var details = new List<ErrorDetail>();
        var contentJson = Content(request.Content, details);

        if (details.Count == 0)
        {
            await MeasureAsync(db, contentJson, null, details, cancellationToken).ConfigureAwait(false);
        }

        return details.Count > 0
            ? (null, DraftErrors.Invalid("the draft body does not match the contract", details))
            : (contentJson, null);
    }

    /// <summary>
    /// Refuses a member the column's own CHECK would refuse: only PostgreSQL knows the bytes <c>jsonb::text</c> holds,
    /// so the exact bound is measured there, after the cheap shape and raw-size checks above have passed.
    /// </summary>
    private static async Task MeasureAsync(
        ITenantDb db,
        string contentJson,
        string? provenanceJson,
        List<ErrorDetail> details,
        CancellationToken cancellationToken)
    {
        if (await DraftSizes.BytesAsync(db, contentJson, cancellationToken).ConfigureAwait(false) > MaxContentBytes)
        {
            details.Add(new ErrorDetail("content", "too_large"));
        }

        if (provenanceJson is not null
            && await DraftSizes.BytesAsync(db, provenanceJson, cancellationToken).ConfigureAwait(false) > MaxProvenanceBytes)
        {
            details.Add(new ErrorDetail("provenance", "too_large"));
        }
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
            var path = exception.Path is { } reported ? TextRules.Brief(reported.TrimStart('$', '.')) : string.Empty;
            return (null, DraftErrors.Invalid(
                $"the body does not match {resource}",
                [new ErrorDetail(path.Length == 0 ? string.Empty : path, "invalid")]));
        }
    }

    private static string Content(DraftContentDto? content, List<ErrorDetail> details)
    {
        var files = content?.Files;
        if (files is null || files.Count < 1 || files.Count > MaxFiles)
        {
            details.Add(new ErrorDetail("content.files", "out_of_range"));
            return "{}";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"content.files[{index}]");

            if (file is null)
            {
                details.Add(new ErrorDetail(prefix, "invalid_type"));
                continue;
            }

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

        var serialized = JsonSerializer.Serialize(content, DraftJson.Options);

        // No byte gate on this text: the serialiser escapes non-ASCII, so a C# count is not the jsonb length. The
        // route's 256 KiB raw-body cap and the shape bounds above bound the work; the column's cast measures exactly.
        return serialized;
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

        if (ExceedsNumericRange(element))
        {
            details.Add(new ErrorDetail("provenance", "too_large"));
            return null;
        }

        // The exact byte bound is measured by PostgreSQL after this: insignificant whitespace in the raw text makes any
        // C# size proxy refuse a value the column accepts. Numbers are bounded first because the jsonb cast raises
        // 22003 (a 500) outside numeric's range and renders a tiny exponent as thousands of digits.
        var text = element.GetRawText();
        return text;
    }

    /// <summary>
    /// True when the numbers in the value are too long, carry an exponent past ±400, or would render (as PostgreSQL
    /// writes a numeric, never in scientific form) to more than the column's 4096 bytes in total. Bounding the token
    /// and exponent keeps every later size computation far from overflow and from numeric's own limits.
    /// </summary>
    internal static bool ExceedsNumericRange(JsonElement element)
    {
        var rendered = 0L;
        return ExceedsNumericRange(element, ref rendered);
    }

    private static bool ExceedsNumericRange(JsonElement element, ref long rendered)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                var length = RenderedLength(JsonMarshal.GetRawUtf8Value(element));
                if (length < 0)
                {
                    return true;
                }

                rendered += length;
                return rendered > MaxProvenanceBytes;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (ExceedsNumericRange(member.Value, ref rendered))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ExceedsNumericRange(item, ref rendered))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// An upper bound of the characters PostgreSQL writes for the number (never below the real count), or -1 when the token
    /// is out of bounds. The token is measured as raw UTF-8 (a JSON number is ASCII) so an oversized one is refused before
    /// any string is built; only a token within the bound is widened, on the stack.
    /// </summary>
    internal static long RenderedLength(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxNumberTokenLength)
        {
            return -1;
        }

        Span<char> buffer = stackalloc char[MaxNumberTokenLength];
        var span = buffer[..System.Text.Encoding.UTF8.GetChars(utf8, buffer)];
        var marker = span.IndexOfAny('e', 'E');
        var mantissa = marker < 0 ? span : span[..marker];
        var exponent = 0;
        if (marker >= 0
            && (!int.TryParse(span[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent > MaxNumberExponent
                || exponent < -MaxNumberExponent))
        {
            return -1;
        }

        // numeric text is never scientific: every integer digit (shifted by the exponent) and max(fraction - exponent, 0)
        // fraction digits are written. Leading zeros only lower the real count, so this never undercounts.
        var negative = mantissa.Length > 0 && mantissa[0] == '-';
        var digits = negative ? mantissa[1..] : mantissa;
        var point = digits.IndexOf('.');
        var integer = point < 0 ? digits.Length : point;
        var fraction = point < 0 ? 0 : digits.Length - point - 1;
        var fractionRendered = Math.Max(0, fraction - exponent);
        return Math.Max(1, integer + exponent) + fractionRendered + (fractionRendered > 0 ? 1 : 0) + (negative ? 1 : 0);
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

    /// <summary>
    /// A repository-relative path under the draft's own 512-character bound: the segment rules are <see cref="RepoPath"/>'s
    /// (no empty, <c>.</c> or <c>..</c> segment, no leading or trailing slash, clean text), never a second copy.
    /// </summary>
    internal static bool IsPath(string? value) =>
        value is { Length: >= 1 and <= MaxPathLength } && RepoPath.IsValid(value);

    /// <summary>
    /// A body pointer: <see cref="FixtureRules.IsValidBodyRef"/>'s shape (a repository path or an artifact id, no
    /// whitespace) under the draft's own 512-character bound, so a pointer is never inline body text.
    /// </summary>
    internal static bool IsBodyRef(string? value) =>
        value is { Length: >= 1 and <= MaxBodyRefLength } && FixtureRules.IsValidBodyRef(value);
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
