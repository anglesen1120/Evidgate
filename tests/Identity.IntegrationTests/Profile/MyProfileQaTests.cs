using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Profile;

[Trait("Category", "Docker")]
public sealed class MyProfileQaTests(DatabaseFixture fixture) : TenantTest(fixture)
{
    private const string ProfilePath = "/v1/me";
    private const string ValidBody = """{"preferred_locale":"en"}""";
    private const int BodyLimit = 1024;

    [Fact]
    public async Task Read_SignedInUser_ReturnsAllStoredFieldsAndStrongVersion()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var stored = await StoredAsync(host, member.UserId);
        stored.Locale.ShouldBeNull();
        stored.DisplayName.ShouldNotBeNullOrEmpty();
        (await ReadAsync(client, member)).ShouldBe(stored);
        await host.ArrangeAsync("update public.\"user\" set display_name = null where id = @id", ("id", member.UserId));
        var withoutName = await StoredAsync(host, member.UserId);
        withoutName.DisplayName.ShouldBeNull();
        withoutName.Version.ShouldBe(stored.Version + 1);
        (await ReadAsync(client, member)).ShouldBe(withoutName);
    }

    [Theory]
    [InlineData("{\"preferred_locale\":null}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":1}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":true}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":[]}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":{}}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":\"VI\"}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":\"EN\"}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":\" vi \"}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":\"en\\n\"}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":\"\"}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{\"preferred_locale\":\"fr\"}", ApiErrorCode.LocaleInvalid)]
    [InlineData("{}", ApiErrorCode.RequestInvalid)]
    [InlineData("[]", ApiErrorCode.RequestInvalid)]
    [InlineData("null", ApiErrorCode.RequestInvalid)]
    [InlineData("\"en\"", ApiErrorCode.RequestInvalid)]
    [InlineData("", ApiErrorCode.RequestInvalid)]
    [InlineData(" \t\r\n", ApiErrorCode.RequestInvalid)]
    [InlineData("{not json", ApiErrorCode.RequestInvalid)]
    public async Task Patch_InvalidBody_RefusesWithoutChangingProfileOrAudit(string body, string code)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        await RefusedAsync(host, member, () => PatchAsync(client, member, body, before.ETag),
            HttpStatusCode.UnprocessableEntity, code);
        await AcceptedAsync(client, member, before, "en");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("user_id")]
    [InlineData("email")]
    [InlineData("display_name")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("preferredLocale")]
    [InlineData("Preferred_Locale")]
    public async Task Patch_ExtraMember_RefusesWholeWriteAndNamesMember(string field)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["preferred_locale"] = "en",
            [field] = "not-patchable",
        });
        await RefusedAsync(host, member, () => PatchAsync(client, member, body, before.ETag),
            HttpStatusCode.UnprocessableEntity, ApiErrorCode.FieldNotPatchable, field);
        await AcceptedAsync(client, member, before, "en");
    }

    [Theory]
    [InlineData("{\"preferred_locale\":\"vi\",\"preferred_locale\":\"en\"}")]
    [InlineData("{\"preferred_locale\":\"en\",\"preferred_locale\":\"en\"}")]
    [InlineData("{\"preferred_locale\":\"fr\",\"preferred_locale\":\"en\"}")]
    [InlineData("{\"preferred_locale\":\"en\",\"preferred_locale\":\"fr\"}")]
    public async Task Patch_DuplicateLocaleMembers_RefusesAmbiguousInput(string body)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        // Duplication is a shape fault of the document: it is refused before any value is judged, so an
        // invalid first value cannot be "fixed" by a valid second one.
        await RefusedAsync(host, member, () => PatchAsync(client, member, body, before.ETag),
            HttpStatusCode.UnprocessableEntity, ApiErrorCode.RequestInvalid, "preferred_locale");
        await AcceptedAsync(client, member, before, "en");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("*")]
    [InlineData("list")]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"2147483648\"")]
    [InlineData("\"soon\"")]
    public async Task Patch_MissingOrInvalidIfMatch_RefusesWithoutWriting(string? header)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        var ifMatch = header == "list" ? before.ETag + ", \"2147483647\"" : header;
        await RefusedAsync(host, member, () => PatchAsync(client, member, ValidBody, ifMatch),
            HttpStatusCode.UnprocessableEntity, ApiErrorCode.RequestInvalid, "If-Match");
        await AcceptedAsync(client, member, before, "en");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Patch_StaleStrongOrWeakVersion_RefusesAndFreshVersionSucceeds(bool weak)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        var after = await AcceptedAsync(client, member, before, "vi");
        var stale = weak ? "W/" + before.ETag : before.ETag;
        await RefusedAsync(host, member, () => PatchAsync(client, member, ValidBody, stale),
            HttpStatusCode.PreconditionFailed, ApiErrorCode.PreconditionFailed);
        // Identity's existing parser also accepts the weak spelling of the current numeric version.
        using var response = await PatchAsync(client, member, ValidBody, weak ? "W/" + after.ETag : after.ETag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(client, member)).ShouldBe(after with { Locale = "en", Version = after.Version + 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Patch_BodyLimit_RejectsOneByteOverAndAcceptsExactLimit(bool unknownLength)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        var exact = ValidBody.PadRight(BodyLimit, ' ');
        Encoding.UTF8.GetByteCount(exact).ShouldBe(BodyLimit);
        await RefusedAsync(host, member, () => PatchAsync(client, member, exact + " ", before.ETag,
            unknownLength: unknownLength), HttpStatusCode.RequestEntityTooLarge, ApiErrorCode.PayloadTooLarge);
        using var accepted = await PatchAsync(client, member, exact, before.ETag, unknownLength: unknownLength);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(client, member)).ShouldBe(before with { Locale = "en", Version = before.Version + 1 });
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Patch_WrongContentType_RefusesAndJsonContentTypeSucceeds(string contentType)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        await RefusedAsync(host, member,
            () => PatchAsync(client, member, ValidBody, before.ETag, contentType: contentType),
            HttpStatusCode.UnprocessableEntity, ApiErrorCode.RequestInvalid, "body");
        await AcceptedAsync(client, member, before, "en");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("foreign")]
    public async Task Patch_InvalidXsrfHeader_RefusesAndCorrectPairSucceeds(string tokenKind)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        using var otherClient = host.CreateSessionClient();
        var other = await host.SignInMemberAsync(otherClient);
        var before = await ReadAsync(client, member);
        var token = tokenKind == "foreign" ? other.Xsrf.RequestToken : "invalid-token";
        await RefusedAsync(host, member, () => PatchAsync(client, member, ValidBody, before.ETag,
            xsrfToken: token, includeXsrf: tokenKind != "missing"), HttpStatusCode.Forbidden, ApiErrorCode.CsrfFailed);
        await AcceptedAsync(client, member, before, "en");
    }

    [Fact]
    public async Task Profile_NoSession_BothRoutesRefuseAndAuthenticatedControlSucceeds()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        using var read = await SessionTestHost.SendAsync(client, HttpMethod.Get, ProfilePath);
        read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(read)).ShouldBe(ApiErrorCode.Unauthenticated);
        await RefusedAsync(host, member, () => PatchAsync(client, null, ValidBody, before.ETag),
            HttpStatusCode.Unauthorized, ApiErrorCode.Unauthenticated);
        await AcceptedAsync(client, member, before, "en");
    }

    [Theory]
    [InlineData("revoked", HttpStatusCode.Unauthorized, ApiErrorCode.SessionRevoked)]
    [InlineData("idle", HttpStatusCode.Unauthorized, ApiErrorCode.SessionExpired)]
    [InlineData("absolute", HttpStatusCode.Unauthorized, ApiErrorCode.SessionExpired)]
    [InlineData("disabled", HttpStatusCode.Forbidden, ApiErrorCode.AccountDisabled)]
    public async Task Profile_InactiveSession_BothRoutesRefuseWithoutWriting(string state, HttpStatusCode status, string code)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var before = await ReadAsync(client, member);
        var written = await AcceptedAsync(client, member, before, "en");
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);
        if (state == "revoked")
        {
            await host.RevokeSessionAsync(sessionId);
        }
        else if (state == "disabled")
        {
            await host.DisableUserAsync(member.UserId);
        }
        else
        {
            var session = await host.SessionRowAsync(sessionId);
            var deadline = state == "idle" ? session.ExpiresAt : session.AbsoluteExpiresAt;
            host.Clock.Advance(deadline - host.Clock.GetUtcNow());
        }

        using var read = await SessionTestHost.SendAsync(client, HttpMethod.Get, ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));
        read.StatusCode.ShouldBe(status);
        (await SessionTestHost.ErrorCodeAsync(read)).ShouldBe(code);
        var stored = await StoredAsync(host, member.UserId);
        await RefusedAsync(host, member, () => PatchAsync(client, member, """{"preferred_locale":"vi"}""", stored.ETag), status, code);
        stored.Locale.ShouldBe(written.Locale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_ForeignUserInQueryOrBody_CannotSelectAnotherAccount(bool sameOrg)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var clientA = host.CreateSessionClient();
        using var clientB = host.CreateSessionClient();
        var a = await host.SignInMemberAsync(clientA);
        var b = sameOrg ? await host.SignInMemberInOrgAsync(clientB, a.OrgId, roleCode: null)
            : await host.SignInMemberAsync(clientB);
        var beforeA = await ReadAsync(clientA, a);
        var beforeB = await ReadAsync(clientB, b);
        var path = ProfilePath + "?id=" + b.UserId + "&user_id=" + b.UserId;
        (await ReadAsync(clientA, a, path)).ShouldBe(beforeA);
        var injected = JsonSerializer.Serialize(new { preferred_locale = "en", id = b.UserId });
        await RefusedAsync(host, a, () => PatchAsync(clientA, a, injected, beforeA.ETag),
            HttpStatusCode.UnprocessableEntity, ApiErrorCode.FieldNotPatchable, "id");
        using var patched = await PatchAsync(clientA, a, ValidBody, beforeA.ETag, path: path);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(clientA, a)).ShouldBe(beforeA with { Locale = "en", Version = beforeA.Version + 1 });
        (await ReadAsync(clientB, b)).ShouldBe(beforeB);
        (await StoredAsync(host, b.UserId)).ShouldBe(beforeB);
        await AcceptedAsync(clientB, b, beforeB, "vi");
        (await StoredAsync(host, a.UserId)).Locale.ShouldBe("en");
    }

    [Fact]
    public async Task Patch_TwoLiveSessionsWithSameVersion_OnlyOneWinsAndVersionStaysMonotonic()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var firstClient = host.CreateSessionClient();
        using var secondClient = host.CreateSessionClient();
        var seeded = await host.SeedMemberAsync();
        using var firstLogin = await host.SignInAsync(firstClient);
        using var secondLogin = await host.SignInAsync(secondClient);
        var first = SignedIn(seeded, firstLogin);
        var second = SignedIn(seeded, secondLogin);
        first.SessionCookie.ShouldNotBe(second.SessionCookie);
        var before = await ReadAsync(firstClient, first);
        (await ReadAsync(secondClient, second)).ShouldBe(before);
        var auditBefore = await AuditCountAsync(host, first);
        var firstWrite = PatchAsync(firstClient, first, """{"preferred_locale":"vi"}""", before.ETag);
        var secondWrite = PatchAsync(secondClient, second, ValidBody, before.ETag);
        await Task.WhenAll(firstWrite, secondWrite);
        using var firstResponse = await firstWrite;
        using var secondResponse = await secondWrite;
        var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };
        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(1);
        statuses.Count(status => status == HttpStatusCode.PreconditionFailed).ShouldBe(1);
        var loser = firstResponse.StatusCode == HttpStatusCode.PreconditionFailed ? firstResponse : secondResponse;
        (await SessionTestHost.ErrorCodeAsync(loser)).ShouldBe(ApiErrorCode.PreconditionFailed);
        var after = await ReadAsync(firstClient, first);
        after.ShouldBe(before with { Locale = firstResponse.StatusCode == HttpStatusCode.OK ? "vi" : "en", Version = before.Version + 1 });
        (await ReadAsync(secondClient, second)).ShouldBe(after);
        (await AuditCountAsync(host, first)).ShouldBe(auditBefore + 1);
        var next = await AcceptedAsync(secondClient, second, after, after.Locale == "vi" ? "en" : "vi");
        (await ReadAsync(firstClient, first)).ShouldBe(next);
        (await StoredAsync(host, first.UserId)).ShouldBe(next);
    }

    private static SignedInMember SignedIn(SeededMember seeded, HttpResponseMessage login)
    {
        login.StatusCode.ShouldBe(HttpStatusCode.Found);
        var cookie = SessionTestHost.SessionCookieValue(login);
        cookie.ShouldNotBeNull();
        return new SignedInMember(seeded.OrgId, seeded.UserId, seeded.Email, cookie, XsrfPair.From(login));
    }

    private static async Task<ProfileState> AcceptedAsync(HttpClient client, SignedInMember member, ProfileState before, string locale)
    {
        var body = JsonSerializer.Serialize(new { preferred_locale = locale });
        using var response = await PatchAsync(client, member, body, before.ETag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await ResponseProfileAsync(response);
        result.ShouldBe(before with { Locale = locale, Version = before.Version + 1 });
        (await ReadAsync(client, member)).ShouldBe(result);
        return result;
    }

    private static async Task RefusedAsync(SessionTestHost host, SignedInMember member,
        Func<Task<HttpResponseMessage>> send, HttpStatusCode status, string code, string? field = null)
    {
        var before = await StoredAsync(host, member.UserId);
        var auditBefore = await AuditCountAsync(host, member);
        using var response = await send();
        response.StatusCode.ShouldBe(status);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(code);
        if (field is not null)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            json.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
                .Any(detail => detail.GetProperty("path").GetString() == field).ShouldBeTrue();
        }

        (await StoredAsync(host, member.UserId)).ShouldBe(before);
        (await AuditCountAsync(host, member)).ShouldBe(auditBefore);
    }

    private static async Task<ProfileState> ReadAsync(HttpClient client, SignedInMember member, string path = ProfilePath)
    {
        using var response = await SessionTestHost.SendAsync(client, HttpMethod.Get, path,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ResponseProfileAsync(response);
    }

    private static async Task<ProfileState> ResponseProfileAsync(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag;
        etag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
        var cache = response.Headers.CacheControl;
        cache.ShouldNotBeNull();
        cache.NoStore.ShouldBeTrue();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        json.RootElement.EnumerateObject().Count().ShouldBe(4);
        var version = int.Parse(etag.Tag.Trim('"'), CultureInfo.InvariantCulture);
        etag.Tag.ShouldBe('"' + version.ToString(CultureInfo.InvariantCulture) + '"');
        return Profile(json.RootElement, version);
    }

    private static async Task<ProfileState> StoredAsync(SessionTestHost host, Guid userId)
    {
        var value = await host.ScalarAsync(
            """
            select json_build_object('id', id, 'email', email, 'display_name', display_name,
                'preferred_locale', preferred_locale, 'version', version)::text
            from public."user" where id = @id
            """, [("id", userId)]);
        var text = value.ShouldBeOfType<string>();
        using var json = JsonDocument.Parse(text);
        return Profile(json.RootElement, json.RootElement.GetProperty("version").GetInt32());
    }

    private static ProfileState Profile(JsonElement root, int version) => new(
        root.GetProperty("id").GetGuid(), root.GetProperty("email").GetString(),
        root.GetProperty("display_name").GetString(), root.GetProperty("preferred_locale").GetString(), version);

    private static Task<long> AuditCountAsync(SessionTestHost host, SignedInMember member) => host.CountAsync(
        "select count(*) from public.audit_log where action = 'user.locale_changed' and org_id = @org",
        ("org", member.OrgId));

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, SignedInMember? member,
        string body, string? ifMatch, string path = ProfilePath, string contentType = "application/json",
        string? xsrfToken = null, bool includeXsrf = true, bool unknownLength = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path);
        if (member is not null)
        {
            request.Headers.Add("Cookie", SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf));
            if (includeXsrf)
            {
                request.Headers.Add(SessionTestHost.XsrfHeaderName, xsrfToken ?? member.Xsrf.RequestToken);
            }
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        request.Content = unknownLength ? new UnknownLengthContent(body) : new StringContent(body, Encoding.UTF8, contentType);
        return await client.SendAsync(request, Token);
    }

    private sealed record ProfileState(Guid Id, string? Email, string? DisplayName, string? Locale, int Version)
    {
        internal string ETag => '"' + Version.ToString(CultureInfo.InvariantCulture) + '"';
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        internal UnknownLengthContent(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, Token).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
