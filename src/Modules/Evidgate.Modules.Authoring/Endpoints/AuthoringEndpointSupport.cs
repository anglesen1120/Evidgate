using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The shared edge of the authoring routes of 13.1.1 §5: the parameter readers every authoring endpoint needs, so the
/// three route files (<c>AuthoringNodeEndpoints</c>, <c>AuthoringChecklistEndpoints</c>,
/// <c>AuthoringManualCaseEndpoints</c>) refuse a malformed request identically instead of each growing its own copy.
/// It is the authoring counterpart of the private helpers of <c>CatalogEndpoints.cs:299-424</c> (the strict, capped body
/// reader of <c>CreateSuiteRevisionAsync</c> and the <c>If-Match</c> parse of <c>IfMatchError</c>), with two additions
/// the authoring contract needs: the <c>"order:&lt;sha256&gt;"</c> reorder precondition (AC2/AC19) and a body reader
/// that refuses a duplicate member or a lone surrogate as a caller error instead of letting the reader throw later
/// (OI-13-116, never a 500).
/// <para>
/// Every member here answers in <see cref="Error"/> terms — never by writing the response — so the delegate returns
/// <c>Result.Failure(error)</c> and the host's <c>ResultEndpointFilter</c> renders the 00 §9.2 envelope once. Nothing in
/// this file touches the tenant, the pipeline or the database: authorization, the tenant scope and the idempotency
/// claim are the C2 pipeline's (00 §5.5 C2).
/// </para>
/// <para>
/// <b>The contract the three route files may rely on</b> (do not add a member without updating this block):
/// <list type="bullet">
/// <item><c>internal const int BodyCap = 64 * 1024</c> — the route-body cap of 13.1.1 §8/AC16.</item>
/// <item><c>internal const int DefaultLimit = 50</c> / <c>MaxLimit = 200</c> — the list default and bound (00 §9.1).</item>
/// <item><c>AuthoringText.MaximumEchoedNameLength = 64</c> — the longest caller-supplied name that reaches a
/// <c>details[].path</c> segment; every echo goes through <c>AuthoringText.Brief</c>, which clips a longer name with an
/// ellipsis (unknown member, pointer segment).</item>
/// <item><c>internal static readonly JsonSerializerOptions RequestJson</c> — snake_case, unmapped members disallowed;
/// the documented request DTOs are deserialised with it.</item>
/// <item><c>internal const string InvalidMemberNamePath = "/&lt;invalid-member-name&gt;"</c> — the stable
/// <c>details[].path</c> for a member name that cannot be echoed (a lone surrogate).</item>
/// <item><c>static string IdempotencyKey(HttpContext context)</c> — the raw <c>Idempotency-Key</c> header (empty when
/// absent; the C2 behaviour owns the 400).</item>
/// <item><c>static string? Blank(string? value)</c> — <c>null</c> for an absent/empty query value.</item>
/// <item><c>static bool TryParseGuid(string? value, out Guid id)</c> — the query/body uuid reader.</item>
/// <item><c>static Error? IfMatchVersion(HttpContext context, out int version)</c> — <c>"&lt;n&gt;"</c>; absent →
/// 422 <c>/If-Match</c> <c>required</c>, malformed → 422 <c>/If-Match</c> <c>invalid</c> (AC19).</item>
/// <item><c>static Error? IfMatchOrder(HttpContext context, out string orderHash)</c> — <c>"order:sha256:&lt;hex&gt;"</c>
/// (the exact <c>AuthoringHashes.OrderHash</c> string, quotes stripped); absent/malformed → 422 <c>/If-Match</c>.</item>
/// <item><c>static Task&lt;(JsonElement Body, Error? Error)&gt; ReadBodyAsync(HttpContext context, CancellationToken
/// cancellationToken)</c> and its <c>(context, int maxBytes, cancellationToken)</c> overload — a strict, capped JSON
/// <b>object</b>: over the cap → 413 <c>payload_too_large</c>; unparsable, duplicate member, lone surrogate or U+0000
/// (in a value or a member name) → 422 <c>request_invalid</c> (reasons <c>invalid_json</c> / <c>invalid_text</c>).</item>
/// <item><c>static Task&lt;(JsonElement Body, Error? Error)&gt; ReadOptionalBodyAsync(HttpContext context, CancellationToken
/// cancellationToken)</c> — the same, answering <c>JsonValueKind.Undefined</c> for a bodiless request (the optional
/// <c>{reason?}</c> of a DELETE).</item>
/// <item><c>static Error? UnknownMembers(JsonElement body, IReadOnlyCollection&lt;string&gt; allowed, string what)</c> —
/// 422 <c>/member</c> <c>unknown_field</c> for the first member outside <c>allowed</c> (root level only; a nested
/// document such as <c>steps</c> is validated by its own schema).</item>
/// <item><c>static Error? LimitError(string? raw, out int limit)</c> — the <c>?limit=</c> reader.</item>
/// <item><c>static void SetVersionETag(HttpContext context, int version)</c> — writes <c>ETag: "&lt;version&gt;"</c>.</item>
/// <item><c>static Error ToError(AuthoringRefusal refusal)</c> and <c>static Result&lt;T&gt; Failure&lt;T&gt;(Error error)</c>
/// / <c>Failure&lt;T&gt;(AuthoringRefusal refusal)</c> — the mapper from the module's own refusal shape to the
/// pipeline's <see cref="Result{T}"/>.</item>
/// </list>
/// </para>
/// </summary>
internal static class AuthoringEndpointSupport
{
    /// <summary>The route-body cap of 13.1.1 §8 (AC16): every authoring route body is at most 64 KiB.</summary>
    internal const int BodyCap = 64 * 1024;

    /// <summary>The list default (00 §9.1, the <c>CatalogEndpoints.DefaultLimit</c> value).</summary>
    internal const int DefaultLimit = 50;

    /// <summary>The list bound (00 §9.1, the <c>CatalogEndpoints.MaxLimit</c> value).</summary>
    internal const int MaxLimit = 200;

    /// <summary>
    /// The one <c>details[].path</c> an unreadable member <b>name</b> is reported under (OI-13-116): a name that is not
    /// valid UTF-16 cannot be echoed in a detail — reading it throws, and writing it into the answer would throw again
    /// while the envelope is serialised — so every such name answers this stable placeholder
    /// (<c>AuthoringContent.InvalidMemberNamePath</c> is the <c>/content/…</c>-prefixed form of the same rule).
    /// </summary>
    internal const string InvalidMemberNamePath = "/<invalid-member-name>";

    /// <summary>
    /// The documented request DTOs of the authoring routes: snake_case (00 §9.1) with unknown members refused, so a
    /// typo is a 422 rather than a silently ignored field. A route that needs a precise per-member 422 reads the raw
    /// body (<see cref="ReadBodyAsync"/>) and validates the shape itself.
    /// </summary>
    internal static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The raw <c>Idempotency-Key</c> header (empty when absent; the C2 behaviour owns the 400 for that).</summary>
    /// <param name="context">The request.</param>
    internal static string IdempotencyKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Request.Headers["Idempotency-Key"].ToString();
    }

    /// <summary>An absent or empty query value reads as <c>null</c> (the catalog list routes' rule).</summary>
    /// <param name="value">The raw query value.</param>
    internal static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Reads a uuid from a query value or a body member.</summary>
    /// <param name="value">The raw text, or null when the member or query key is absent.</param>
    /// <param name="id">The parsed id on success.</param>
    internal static bool TryParseGuid(string? value, out Guid id)
    {
        id = Guid.Empty;

        return !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out id);
    }

    /// <summary>
    /// Reads the <c>If-Match</c> entity precondition (AC19): absent → 422 <c>/If-Match</c> <c>required</c>, anything
    /// but a positive integer version (optionally weak and quoted) → 422 <c>/If-Match</c> <c>invalid</c>.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="version">The version the caller read, on success.</param>
    internal static Error? IfMatchVersion(HttpContext context, out int version)
    {
        ArgumentNullException.ThrowIfNull(context);

        version = 0;
        var raw = Unquote(context.Request.Headers.IfMatch.ToString());

        if (raw.Length == 0)
        {
            return Error.Invalid(
                "If-Match is required: send the ETag of the entity you read (00 §9.1)",
                [new ErrorDetail("/If-Match", "required")]);
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1
            ? null
            : Error.Invalid(
                "If-Match must be the ETag this endpoint issued: the entity's version (00 §9.1)",
                [new ErrorDetail("/If-Match", "invalid")]);
    }

    /// <summary>
    /// Reads the <c>If-Match</c> sibling-order precondition of a reorder (AC2/AC19): the value is the
    /// <c>"order:sha256:&lt;hex&gt;"</c> tag <see cref="Domain.AuthoringHashes.OrderHash"/> issued over the sorted
    /// <c>(id, version)</c> pairs the caller read. The answer is the tag without quotes or a <c>W/</c> prefix, exactly
    /// the string that helper returns, so the command compares it ordinally and never re-derives it.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="orderHash">The order ETag the caller presented, on success.</param>
    internal static Error? IfMatchOrder(HttpContext context, out string orderHash)
    {
        ArgumentNullException.ThrowIfNull(context);

        orderHash = string.Empty;
        var raw = Unquote(context.Request.Headers.IfMatch.ToString());

        if (raw.Length == 0)
        {
            return Error.Invalid(
                "If-Match is required: send the order ETag of the siblings you read (00 §9.1)",
                [new ErrorDetail("/If-Match", "required")]);
        }

        // `order:` + the 00 §7 digest shape (`sha256:<lowercase hex>`); a bare digest without the prefix is refused
        // too, so an entity ETag can never be mistaken for an order ETag.
        const string prefix = Domain.AuthoringHashes.OrderPrefix + "sha256:";
        var hex = raw.StartsWith(prefix, StringComparison.Ordinal) ? raw[prefix.Length..] : string.Empty;

        if (hex.Length != 64 || !hex.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return Error.Invalid(
                "If-Match must be the order ETag this endpoint issued: \"order:sha256:<hex>\" over the siblings you read (00 §9.1)",
                [new ErrorDetail("/If-Match", "invalid")]);
        }

        orderHash = raw;

        return null;
    }

    /// <summary>
    /// Reads a route body as a strict, capped JSON object (<see cref="BodyCap"/>).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>The parsed object, or the 413/422 the caller must return unchanged.</returns>
    internal static Task<(JsonElement Body, Error? Error)> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken) =>
        ReadBodyAsync(context, BodyCap, cancellationToken);

    /// <summary>Reads a route body as a strict JSON object capped at <paramref name="maxBytes"/> bytes.</summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The byte cap; over it the answer is 413 <c>payload_too_large</c>.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>The parsed object, or the 413/422 the caller must return unchanged.</returns>
    internal static Task<(JsonElement Body, Error? Error)> ReadBodyAsync(
        HttpContext context,
        int maxBytes,
        CancellationToken cancellationToken) =>
        ReadAsync(context, maxBytes, required: true, cancellationToken);

    /// <summary>
    /// Reads an optional route body (the <c>{reason?}</c> of a DELETE): a request with no body answers
    /// <see cref="JsonValueKind.Undefined"/> and no error, everything else is <see cref="ReadBodyAsync(HttpContext, CancellationToken)"/>.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    internal static Task<(JsonElement Body, Error? Error)> ReadOptionalBodyAsync(HttpContext context, CancellationToken cancellationToken) =>
        ReadAsync(context, BodyCap, required: false, cancellationToken);

    /// <summary>
    /// Refuses the first root-level member that is not in <paramref name="allowed"/> with the 422 that names it
    /// (<c>details[].path = /member</c>, <c>reason = unknown_field</c>). Root level only on purpose: a nested document
    /// (<c>steps</c>) is validated against its own schema, whose problems are rebased by its validator.
    /// </summary>
    /// <param name="body">The parsed request body.</param>
    /// <param name="allowed">The members the route accepts.</param>
    /// <param name="what">The entity name used in the message, e.g. <c>feature node</c>.</param>
    internal static Error? UnknownMembers(JsonElement body, IReadOnlyCollection<string> allowed, string what)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        if (body.ValueKind != JsonValueKind.Object)
        {
            return Error.Invalid("the body must be a JSON object", [new ErrorDetail(string.Empty, "invalid_type")]);
        }

        foreach (var member in body.EnumerateObject())
        {
            string name;
            try
            {
                name = member.Name;
            }
            catch (InvalidOperationException)
            {
                // The name is not valid UTF-16, so it cannot be echoed; the body reader already refuses this case, and
                // answering the placeholder keeps a future caller that skipped ReadBodyAsync honest.
                return Error.Invalid(
                    $"the body carries a member name that is not valid text, so it is not a field of a {what}",
                    [new ErrorDetail(InvalidMemberNamePath, "invalid_text")]);
            }

            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                return Error.Invalid(
                    $"'{Domain.AuthoringText.Brief(name)}' is not a field of a {what}",
                    [new ErrorDetail("/" + Domain.AuthoringText.Brief(name), "unknown_field")]);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads <c>?limit=</c>: absent → <see cref="DefaultLimit"/>, anything outside 1..<see cref="MaxLimit"/> → 422
    /// <c>/limit</c> <c>out_of_range</c>.
    /// </summary>
    /// <param name="raw">The raw query value.</param>
    /// <param name="limit">The page size to use.</param>
    internal static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("/limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }

    /// <summary>Writes the entity ETag of AC19 (<c>"&lt;version&gt;"</c>) on the response, for a 200 or a 201.</summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="version">The entity's version after the write.</param>
    internal static void SetVersionETag(HttpContext context, int version)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");
    }

    /// <summary>
    /// Maps the module's own refusal shape (the <c>AuthoringWriteResult</c> failure of a writer call made through
    /// <see cref="IAuthoringVersionWriter"/>) to the 00 §9.2 <see cref="Error"/> the pipeline renders: same code, same
    /// status, same per-field details, and the retryable flag derived from the one status that carries it.
    /// </summary>
    /// <param name="refusal">The refusal to map.</param>
    internal static Error ToError(AuthoringRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new Error(
            refusal.Code,
            refusal.Message,
            refusal.Status,
            Retryable: refusal.Status == StatusCodes.Status503ServiceUnavailable,
            Details: refusal.Details is { Count: > 0 } details
                ? [.. details.Select(detail => new ErrorDetail(detail.Path, detail.Reason))]
                : null);
    }

    /// <summary>A failed result the caller returns unchanged.</summary>
    /// <typeparam name="T">The success value's type.</typeparam>
    /// <param name="error">The failure.</param>
    internal static Result<T> Failure<T>(Error error) => Result.Failure<T>(error);

    /// <summary>A failed result for a writer refusal (see <see cref="ToError"/>).</summary>
    /// <typeparam name="T">The success value's type.</typeparam>
    /// <param name="refusal">The refusal to map.</param>
    internal static Result<T> Failure<T>(AuthoringRefusal refusal) => Result.Failure<T>(ToError(refusal));

    /// <summary>Strips a weak prefix and the surrounding quotes from an <c>If-Match</c> value.</summary>
    /// <param name="raw">The header value.</param>
    private static string Unquote(string raw)
    {
        var value = raw.Trim();

        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..].TrimStart();
        }

        return value.Trim('"');
    }

    /// <summary>
    /// The strict, capped body read shared by both entry points: a JSON <b>object</b> only, its tree proven storable (no
    /// text PostgreSQL would refuse — a lone surrogate or U+0000 — in a name or a string value) before a handler can
    /// touch it.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The byte cap.</param>
    /// <param name="required">Whether a bodiless request is a 422 instead of an empty answer.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    private static async Task<(JsonElement Body, Error? Error)> ReadAsync(
        HttpContext context,
        int maxBytes,
        bool required,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength > maxBytes)
        {
            return (default, TooLarge(maxBytes));
        }

        // The buffer is sized up front to the body it may hold (the declared length, or the cap for a chunked body),
        // and every chunk is measured BEFORE it is written: a streamed body of any size therefore never allocates more
        // than the cap, while `MemoryStream`'s own doubling would reach 128 KiB — a large-object-heap buffer — for a
        // body of a few bytes over 64 KiB before the old post-write check could refuse it.
        var capacity = maxBytes;
        if (context.Request.ContentLength is { } declaredLength)
        {
            // A declared length is the body's exact size (a longer one was refused above, so it never exceeds the cap):
            // sizing the buffer to it means a within-cap body never grows the stream, and a bodiless request allocates
            // nothing. Only a chunked request, whose length is unknown, may hold the cap.
            capacity = declaredLength <= 0 ? 0 : (int)declaredLength;
        }

        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return (default, TooLarge(maxBytes));
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            return required
                ? (default, Error.Invalid("the body is required", [new ErrorDetail(string.Empty, "required")]))
                : (default, null);
        }

        JsonDocument document;
        try
        {
            // AllowDuplicateProperties = false (10.0) is what makes a duplicate member a caller error: without it the
            // reader keeps the last value silently, and the idempotency hash of AC18 would not describe the body.
            // The parsed memory is the stream's own buffer (the stream is public and ours, so no UnauthorizedAccess
            // path exists): `Parse` copies the bytes it keeps, so `ToArray` would be a second, avoidable copy.
            document = JsonDocument.Parse(
                buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            return (default, Error.Invalid(
                "the body is not valid JSON",
                [new ErrorDetail(string.Empty, "invalid_json")]));
        }
        catch (InvalidOperationException)
        {
            // Duplicate detection forces the reader to materialise every member name, and a name that is not valid
            // UTF-16 throws there instead of at the point a value would be read (OI-13-116).
            return (default, Error.Invalid(
                "the body carries text that is not valid UTF-16",
                [new ErrorDetail(InvalidMemberNamePath, "invalid_text")]));
        }

        using (document)
        {
            var body = document.RootElement.Clone();

            if (body.ValueKind != JsonValueKind.Object)
            {
                return (default, Error.Invalid("the body must be a JSON object", [new ErrorDetail(string.Empty, "invalid_type")]));
            }

            return UnstorableText(body, string.Empty) is { } unstorable ? (default, unstorable) : (body, null);
        }
    }

    /// <summary>413 <c>payload_too_large</c>; the message names the cap, never the payload (00 §9.2).</summary>
    /// <param name="maxBytes">The byte cap.</param>
    private static Error TooLarge(int maxBytes) => new(
        ApiErrorCode.PayloadTooLarge,
        string.Create(CultureInfo.InvariantCulture, $"the request body is larger than {maxBytes} bytes"),
        StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Walks the parsed body once and answers the first string value or member name that PostgreSQL cannot store: text
    /// that is not valid UTF-16 (a lone surrogate, e.g. <c>{"title":"\ud800"}</c>), or text carrying U+0000, which
    /// <c>text</c> (22021) and <c>jsonb</c> (22P05) both refuse. Refusing it here means no handler ever renders a 500
    /// for a caller's own bytes (OI-13-116, security S-1/S-2); the walk is bounded by the reader's depth limit and the
    /// 64 KiB cap.
    /// </summary>
    /// <param name="element">The element to inspect.</param>
    /// <param name="path">The JSON pointer of that element, e.g. <c>/steps/0/action</c>.</param>
    private static Error? UnstorableText(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    string name;
                    try
                    {
                        name = member.Name;
                    }
                    catch (InvalidOperationException)
                    {
                        return Error.Invalid(
                            "the body carries a member name that is not valid UTF-16",
                            [new ErrorDetail(InvalidMemberNamePath, "invalid_text")]);
                    }

                    if (Domain.AuthoringText.ContainsNul(name))
                    {
                        // A name that carries a control character is not echoed into a pointer either: the placeholder
                        // is the one path an unrepresentable name answers everywhere in this module.
                        return Error.Invalid(
                            "the body carries a member name with a NUL (U+0000) character, which PostgreSQL cannot store",
                            [new ErrorDetail(InvalidMemberNamePath, "invalid_text")]);
                    }

                    // The pointer segment is clipped to the same 64 characters every echoed caller name is (Brief), so a
                    // 4 096-character member name never becomes a 4 096-character detail path.
                    if (UnstorableText(member.Value, path + "/" + Domain.AuthoringText.Brief(name)) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (UnstorableText(item, string.Create(CultureInfo.InvariantCulture, $"{path}/{index}")) is { } itemError)
                    {
                        return itemError;
                    }

                    index++;
                }

                return null;

            case JsonValueKind.String:
                string text;
                try
                {
                    text = element.GetString()!;
                }
                catch (InvalidOperationException)
                {
                    return Error.Invalid(
                        "the body carries text that is not valid UTF-16",
                        [new ErrorDetail(path, "invalid_text")]);
                }

                return Domain.AuthoringText.ContainsNul(text)
                    ? Error.Invalid(
                        "the body carries text with a NUL (U+0000) character, which PostgreSQL cannot store",
                        [new ErrorDetail(path, "invalid_text")])
                    : null;

            default:
                return null;
        }
    }
}
