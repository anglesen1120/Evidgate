using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Profile;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The signed-in account's own profile: <c>GET /v1/me</c> and <c>PATCH /v1/me</c> with the one
/// member the account may change, <c>preferred_locale</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pre-tenant, like the linking routes beside them.</b> Both routes run before any tenant
/// exists: the caller is the session cookie and nothing else, so neither template carries a user
/// id. <see cref="ISessionRequestAuthenticator"/> resolves the cookie and answers its own lifecycle
/// refusals (401 <c>unauthenticated</c> / <c>session_expired</c> / <c>session_revoked</c>, 403
/// <c>account_disabled</c>) out of one statement, and the handler hands the LIVE cookie's proof to
/// the profile store, whose definer resolves the row from that proof. A route can therefore not
/// address another account, and no handler ever sees the cookie value itself.
/// </para>
/// <para>
/// <b>The version travels in the headers.</b> A read publishes the row's <c>version</c> as its
/// strong <c>ETag</c>; a write requires that value back as <c>If-Match</c> — absent or unparsable
/// is 422, a version the row no longer has is 412 — and answers with the new one, exactly as the
/// membership routes do. Nothing in the body carries a version, so a client cannot think it wrote
/// with one it never read.
/// </para>
/// <para>
/// <b>One member, refused by name.</b> The body is read bounded, must be <c>application/json</c> (any
/// other media type is 422 <c>request_invalid</c> with <c>body</c> in <c>error.details</c>, the answer
/// <c>POST /v1/auth/invite-token</c> gives the same body), and is judged member by name first: any
/// name other than <c>preferred_locale</c> answers 422 <c>field_not_patchable</c> for that name (a
/// valid locale alongside it does not save the request), and a name sent twice answers 422
/// <c>request_invalid</c> with <c>duplicate</c>, before any value is judged — so an invalid first
/// value can never be overwritten into acceptance by a valid second one. Only then is the single
/// value judged: a locale outside <c>vi</c> / <c>en</c> answers 422 <c>locale_invalid</c>. Shape
/// refusals come before the precondition, as the pipeline's validation stage does for a business
/// route.
/// </para>
/// <para>
/// <b>CSRF is enforced for the write, not declared.</b> The <c>PATCH</c> is a cookie-authenticated
/// mutation, so the host-wide <c>SessionXsrfMiddleware</c> requires the antiforgery pair before
/// this handler runs (absent or mismatched: 403 <c>csrf_failed</c>). The read is a safe method and
/// outside the rule.
/// </para>
/// <para>
/// <b>No shared cache.</b> Both answers are the caller's own data, so every response sets
/// <c>Cache-Control: no-store</c>.
/// </para>
/// </remarks>
internal static class MyProfileEndpoints
{
    /// <summary>Every answer's cache policy: a profile is never reusable by a shared cache.</summary>
    private const string NoStore = "no-store";

    /// <summary>The largest body the write accepts: one short string plus envelope slack.</summary>
    internal const int MaxBodyBytes = 1024;

    private const string LocaleMember = "preferred_locale";

    /// <summary>The longest member name a refusal echoes back: enough for any real name, small in the envelope.</summary>
    private const int MaxEchoedMemberChars = 64;

    /// <summary>Maps the two routes onto the <c>/v1</c> group and returns it.</summary>
    /// <param name="group">The <c>/v1</c> group the module's endpoints are mapped on.</param>
    /// <returns>The same group, so one composition applies to every route on it.</returns>
    internal static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // No `.Accepts<T>` on the write: that metadata makes ASP.NET answer 415 for a non-JSON body
        // before the handler runs, while this contract's answer is the bounded reader's 422
        // `request_invalid` (path `body`), as `POST /v1/auth/invite-token` gives the same body. The body
        // is read here rather than bound so an unknown member is refused by name, not ignored.
        // Both routes are TENANTLESS: they sit on the /v1 group, which the host wraps in the tenant filter, and
        // that filter refuses an authenticated request that names no org. The metadata is what tells it to publish
        // the principal and stand aside, exactly as the org list and create routes declare.
        group.MapGet("/me", ReadAsync)
            .Produces<UserProfileDto>(StatusCodes.Status200OK)
            .WithMetadata(TenantlessEndpointMetadata.Instance)
            .WithSummary("The signed-in account's own profile, with its version as the ETag");

        group.MapPatch("/me", PatchAsync)
            .Produces<UserProfileDto>(StatusCodes.Status200OK)
            .WithMetadata(TenantlessEndpointMetadata.Instance)
            .WithSummary("Change the account's preferred language against its ETag (If-Match)");

        return group;
    }

    /// <summary><c>GET /v1/me</c>: the caller's own profile, or the authenticator's 401/403.</summary>
    private static async Task<Result<UserProfileDto>> ReadAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        GetMyProfileQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(query);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, refusal) = await ProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure<UserProfileDto>(refusal);
        }

        var result = await query.ExecuteAsync(proof, cancellationToken).ConfigureAwait(false);

        return WithETag(context, result);
    }

    /// <summary><c>PATCH /v1/me</c>: one member, an <c>If-Match</c> version and the new <c>ETag</c>.</summary>
    private static async Task<Result<UserProfileDto>> PatchAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        UpdateMyProfileCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(command);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, refusal) = await ProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure<UserProfileDto>(refusal);
        }

        var (document, bodyError) = await BoundedJsonBody
            .ReadAsync(context, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        string? locale;
        using (document)
        {
            if (document is null)
            {
                return Result.Failure<UserProfileDto>(bodyError!);
            }

            if (BodyError(document.RootElement, out locale) is { } shapeError)
            {
                return Result.Failure<UserProfileDto>(shapeError);
            }
        }

        if (IfMatchError(context, out var expectedVersion) is { } preconditionError)
        {
            return Result.Failure<UserProfileDto>(preconditionError);
        }

        var result = await command
            .ExecuteAsync(proof, locale!, expectedVersion, cancellationToken)
            .ConfigureAwait(false);

        return WithETag(context, result);
    }

    /// <summary>
    /// The one member the body may carry, judged by name before by value: an unknown member is
    /// refused for itself even when a valid locale travels beside it, so a client learns that the
    /// field it sent is not part of this contract instead of believing it was written. A name sent
    /// twice is refused before any value is judged, so an invalid first value can never be
    /// overwritten into acceptance by a valid second one.
    /// </summary>
    /// <param name="root">The parsed body.</param>
    /// <param name="locale">The validated language, when the body carried one.</param>
    private static Error? BodyError(JsonElement root, out string? locale)
    {
        locale = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return BoundedJsonBody.Invalid("the request body must be a JSON object");
        }

        var member = default(JsonProperty);
        var seen = false;

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, LocaleMember, StringComparison.Ordinal))
            {
                return FieldNotPatchable(property.Name);
            }

            // `JsonDocument` keeps every occurrence of a repeated name, so the second one is a shape
            // fault of the document: the client's intent is ambiguous and no value is read yet.
            if (seen)
            {
                return DuplicateMember();
            }

            member = property;
            seen = true;
        }

        if (!seen)
        {
            return Error.Invalid(
                "preferred_locale is required",
                [new ErrorDetail(LocaleMember, "required")]);
        }

        if (member.Value.ValueKind != JsonValueKind.String)
        {
            return LocaleInvalid();
        }

        locale = member.Value.GetString();

        // The column's own check constraint carries the same two values, so a third one is refused
        // here with a field detail instead of reaching the database as a 500.
        return locale is "vi" or "en" ? null : LocaleInvalid();
    }

    /// <summary>
    /// Parses <c>If-Match</c> into the row's <c>version</c>, in the shapes the other PATCH routes
    /// accept (<c>"3"</c>, <c>W/"3"</c>, <c>3</c>). An absent header is 422 — this contract, like
    /// every other write in the platform, requires the precondition — and so is a value that is not
    /// a positive integer.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="version">The version the caller read.</param>
    private static Error? IfMatchError(HttpContext context, out int version)
    {
        version = 0;
        var raw = context.Request.Headers.IfMatch.ToString();

        if (string.IsNullOrEmpty(raw))
        {
            return Error.Invalid(
                "If-Match is required: send the ETag of the profile you read",
                [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) || version < 1)
        {
            return Error.Invalid(
                "If-Match must be the ETag this endpoint issued: the profile's version",
                [new ErrorDetail("If-Match", "invalid")]);
        }

        return null;
    }

    /// <summary>
    /// Publishes the profile's <c>version</c> as the response's strong <c>ETag</c> (so the next write
    /// can carry it back in <c>If-Match</c>). A failure carries none.
    /// </summary>
    /// <param name="context">The request whose response is being written.</param>
    /// <param name="result">The handler's outcome.</param>
    private static Result<UserProfileDto> WithETag(HttpContext context, Result<MyProfile> result)
    {
        if (!result.IsSuccess)
        {
            return Result.Failure<UserProfileDto>(result.Error!);
        }

        var profile = result.SuccessValue as MyProfile
            ?? throw new InvalidOperationException("a successful profile result carried no profile");

        context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{profile.Version}\"");

        return Result.Success(new UserProfileDto(
            profile.Id,
            profile.Email,
            profile.DisplayName,
            profile.PreferredLocale));
    }

    /// <summary>
    /// The acting session's proof, or the refusal to answer. The authenticator memoizes its outcome
    /// per request, and a live outcome without a proof is a composition bug, never a 401.
    /// </summary>
    /// <param name="context">The request whose cookie is authenticated.</param>
    /// <param name="authenticator">The deployment's authenticator.</param>
    /// <param name="cancellationToken">Request abort.</param>
    private static async Task<(byte[] Proof, Error? Refusal)> ProofAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

        if (outcome.Refusal is { } refusal)
        {
            return ([], refusal);
        }

        return outcome.GetSessionProof() is { } proof ? (proof, null) : ([], SessionErrors.Unavailable);
    }

    /// <summary>422 <c>field_not_patchable</c>: the body named a member this route does not write.</summary>
    /// <param name="member">The member the caller sent, echoed back in the message and the detail path.</param>
    private static Error FieldNotPatchable(string member)
    {
        // The name is caller-controlled, so the echoed copy is bounded: 64 characters identify any real
        // member and keep the envelope small. The cut never splits a surrogate pair, so the message stays
        // valid UTF-16 whatever the caller sent.
        var length = Math.Min(member.Length, MaxEchoedMemberChars);
        if (length < member.Length && char.IsHighSurrogate(member[length - 1]))
        {
            length--;
        }

        var echoed = member[..length];

        return new(
            ApiErrorCode.FieldNotPatchable,
            $"{echoed} is not patchable on this route; only preferred_locale is",
            StatusCodes.Status422UnprocessableEntity,
            Retryable: false,
            Details: [new ErrorDetail(echoed, "not_patchable")]);
    }

    /// <summary>
    /// 422 <c>request_invalid</c>: the body sent <c>preferred_locale</c> more than once, so no value of it
    /// can be taken as the client's choice.
    /// </summary>
    private static Error DuplicateMember() => Error.Invalid(
        $"{LocaleMember} appears more than once; send it once",
        [new ErrorDetail(LocaleMember, "duplicate")]);

    /// <summary>422 <c>locale_invalid</c>: the value is not one of the two languages the contract carries.</summary>
    private static Error LocaleInvalid() => new(
        ApiErrorCode.LocaleInvalid,
        "preferred_locale must be 'vi' or 'en'",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: [new ErrorDetail(LocaleMember, "invalid")]);
}

/// <summary>
/// The signed-in account's own profile as <c>GET /v1/me</c> answers it, and as both routes answer
/// it after a write.
/// </summary>
/// <param name="Id">The account's user id.</param>
/// <param name="Email">The provider-verified address on the account, as the provider gave it.</param>
/// <param name="DisplayName">The provider profile name, or null when the provider supplied none.</param>
/// <param name="PreferredLocale">
/// The chosen language (<c>vi</c> / <c>en</c>), or null until the account has chosen one.
/// </param>
internal sealed record UserProfileDto(
    Guid Id,
    string Email,
    string? DisplayName,
    string? PreferredLocale);
