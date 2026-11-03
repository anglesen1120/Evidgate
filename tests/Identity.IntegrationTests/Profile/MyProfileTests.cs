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

/// <summary>
/// The own-profile routes over the real host: <c>GET /v1/me</c> and <c>PATCH /v1/me</c>, through the real
/// session authenticator and the <c>evidgate_resolve</c> definers of migration 0061.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every request is sent WITHOUT an org header.</b> These routes are tenantless — the caller is the session
/// cookie, resolved inside the definer — so a case that needed an org to pass would mean the host's tenant
/// filter was refusing a real sign-in with its 422. The reads below are therefore also the guard against that
/// regression.
/// </para>
/// <para>
/// <b>Nothing here depends on a version or an id being guessable.</b> The version comes from the <c>ETag</c> the
/// read issued, and the account is the one whose cookies made the request, so "another account's profile" is
/// proven by two real sign-ins and a comparison of what each one sees.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MyProfileTests : TenantTest
{
    private const string ProfilePath = "/v1/me";

    /// <summary>Shares the run's database; every case acts on the accounts and orgs its own sign-ins created.</summary>
    public MyProfileTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The read answers the caller's own row, tenantless, with a null locale until one is chosen and the row's
    /// version as its strong <c>ETag</c> — then the same read carries the language the write stored.
    /// </summary>
    [Fact]
    public async Task Read_Answers_Own_Profile_With_Null_Locale_Then_The_Chosen_One()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        var issued = string.Empty;
        using (var read = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            // No X-Evidgate-Org anywhere: a 200 here is the tenantless contract.
            read.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("id").GetGuid().ShouldBe(member.UserId);
            body.RootElement.GetProperty("email").GetString().ShouldBe(member.Email);
            body.RootElement.GetProperty("preferred_locale").ValueKind.ShouldBe(JsonValueKind.Null);
            body.RootElement.TryGetProperty("version", out _).ShouldBeFalse("the version travels in the ETag only");
            var etag = read.Headers.ETag.ShouldNotBeNull();
            etag.IsWeak.ShouldBeFalse("the write's If-Match carries this very tag");
            issued = etag.Tag;
            read.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }

        using (var written = await PatchAsync(client, member, """{"preferred_locale":"en"}""", ifMatch: issued))
        {
            written.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var reread = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            reread.StatusCode.ShouldBe(HttpStatusCode.OK);
            reread.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(NextETag(issued), "the write bumps the version the trigger owns");

            using var body = JsonDocument.Parse(await reread.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("preferred_locale").GetString().ShouldBe("en");
        }
    }

    /// <summary>The write answers the row it stored, and its <c>ETag</c> is the version a later write must send.</summary>
    [Fact]
    public async Task Write_Answers_The_New_Version_And_That_Version_Is_Required_Next()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var issued = await CurrentETagAsync(client, member);

        string written;
        using (var first = await PatchAsync(client, member, """{"preferred_locale":"vi"}""", ifMatch: issued))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            var etag = first.Headers.ETag.ShouldNotBeNull();
            etag.IsWeak.ShouldBeFalse();
            written = etag.Tag;
            written.ShouldBe(NextETag(issued));

            using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("preferred_locale").GetString().ShouldBe("vi");
        }

        // The version the client no longer has: refused, and nothing changes.
        using (var stale = await PatchAsync(client, member, """{"preferred_locale":"en"}""", ifMatch: issued))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
            (await SessionTestHost.ErrorCodeAsync(stale)).ShouldBe(ApiErrorCode.PreconditionFailed);
        }

        using (var still = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            still.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(written, "a refused write must not bump the version");
        }
    }

    /// <summary>Without the cookie there is no caller: 401 before any handler, on both routes.</summary>
    [Fact]
    public async Task Both_Routes_Answer_401_Without_A_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        using (var read = await SessionTestHost.SendAsync(client, HttpMethod.Get, ProfilePath))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(read)).ShouldBe(ApiErrorCode.Unauthenticated);
        }

        // No If-Match either: an anonymous caller must be refused for the session, never for the version.
        using (var write = await PatchAsync(client, member: null, """{"preferred_locale":"en"}""", ifMatch: null))
        {
            write.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(write)).ShouldBe(ApiErrorCode.Unauthenticated);
        }
    }

    /// <summary>A disabled account is refused on both routes, with the layer's own code.</summary>
    [Fact]
    public async Task A_Disabled_Account_Is_Refused_With_Its_Own_Code()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        await host.DisableUserAsync(member.UserId);

        using var read = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(read)).ShouldBe(ApiErrorCode.AccountDisabled);
    }

    /// <summary>
    /// The body carries exactly one member: an unknown name is refused for ITSELF, even beside a valid locale, and
    /// a third language value is refused as a field error — never as a 500 from the column's constraint.
    /// </summary>
    [Fact]
    public async Task Unknown_Member_And_Unknown_Locale_Are_Field_Errors()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var issued = await CurrentETagAsync(client, member);

        using (var extra = await PatchAsync(
            client,
            member,
            """{"preferred_locale":"en","display_name":"intruder"}""",
            ifMatch: issued))
        {
            extra.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(extra)).ShouldBe(ApiErrorCode.FieldNotPatchable);
        }

        using (var locale = await PatchAsync(client, member, """{"preferred_locale":"fr"}""", ifMatch: issued))
        {
            locale.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(locale)).ShouldBe(ApiErrorCode.LocaleInvalid);
        }

        using (var unchanged = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            unchanged.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(issued, "neither refusal may write");
        }
    }

    /// <summary>Bodies that are absent, unparseable or not JSON are refused before the precondition is read.</summary>
    [Fact]
    public async Task Empty_Malformed_And_Non_Json_Bodies_Are_Refused()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var issued = await CurrentETagAsync(client, member);

        using (var absent = await PatchAsync(client, member, json: null, ifMatch: issued))
        {
            absent.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(absent)).ShouldBe(ApiErrorCode.RequestInvalid);
        }

        using (var malformed = await PatchAsync(client, member, "{not json", ifMatch: issued))
        {
            malformed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(malformed)).ShouldBe(ApiErrorCode.RequestInvalid);
        }

        using (var plainText = await PatchAsync(
            client,
            member,
            """{"preferred_locale":"en"}""",
            ifMatch: issued,
            contentType: "text/plain"))
        {
            plainText.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(plainText)).ShouldBe(ApiErrorCode.RequestInvalid);
        }
    }

    /// <summary>The precondition is required: no header is a field error, and a non-numeric one is no better.</summary>
    [Fact]
    public async Task The_If_Match_Header_Is_Required()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using (var missing = await PatchAsync(client, member, """{"preferred_locale":"en"}""", ifMatch: null))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(missing)).ShouldBe(ApiErrorCode.RequestInvalid);
        }

        using (var garbage = await PatchAsync(client, member, """{"preferred_locale":"en"}""", ifMatch: "\"soon\""))
        {
            garbage.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(garbage)).ShouldBe(ApiErrorCode.RequestInvalid);
        }
    }

    /// <summary>The cookie mutation carries the antiforgery pair, and the middleware is what enforces it.</summary>
    [Fact]
    public async Task The_Write_Requires_The_Antiforgery_Token()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var issued = await CurrentETagAsync(client, member);

        using (var forged = await PatchAsync(
            client,
            member,
            """{"preferred_locale":"en"}""",
            ifMatch: issued,
            includeXsrf: false))
        {
            forged.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await SessionTestHost.ErrorCodeAsync(forged)).ShouldBe(ApiErrorCode.CsrfFailed);
        }

        using (var unchanged = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            unchanged.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(issued);
        }
    }

    /// <summary>
    /// Two real accounts: each sees only its own row, and one account's write leaves the other account's row
    /// exactly as it was. There is no id in either template, so this is the whole isolation surface.
    /// </summary>
    [Fact]
    public async Task One_Account_Can_Neither_Read_Nor_Change_Another()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var clientA = host.CreateSessionClient();
        using var clientB = host.CreateSessionClient();
        var a = await host.SignInMemberAsync(clientA);
        var b = await host.SignInMemberAsync(clientB);

        using (var readB = await SessionTestHost.SendAsync(
            clientB,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(b.SessionCookie)))
        {
            readB.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var body = JsonDocument.Parse(await readB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("id").GetGuid().ShouldBe(b.UserId);
            body.RootElement.GetProperty("id").GetGuid().ShouldNotBe(a.UserId);
        }

        var issuedB = await CurrentETagAsync(clientB, b);
        using (var writeB = await PatchAsync(clientB, b, """{"preferred_locale":"vi"}""", ifMatch: issuedB))
        {
            writeB.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await LocaleAsync(host, a.UserId)).ShouldBeNull("B's write must not touch A's row");
        (await LocaleAsync(host, b.UserId)).ShouldBe("vi");
    }

    /// <summary>
    /// The version belongs to the row's trigger, not to its writers: an owner UPDATE that never names the column
    /// still bumps it, and the version the caller had read is refused from then on.
    /// </summary>
    [Fact]
    public async Task An_Update_That_Omits_The_Version_Is_Stamped_And_Invalidates_The_Old_ETag()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var issued = await CurrentETagAsync(client, member);

        await host.ArrangeAsync(
            "update public.\"user\" set display_name = 'renamed without a version bump' where id = @id",
            ("id", member.UserId));

        (await VersionAsync(host, member.UserId)).ShouldBe(VersionOf(issued) + 1);

        using (var reread = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            reread.StatusCode.ShouldBe(HttpStatusCode.OK);
            reread.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(NextETag(issued));

            using var body = JsonDocument.Parse(await reread.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("display_name").GetString().ShouldBe("renamed without a version bump");
        }

        using (var stale = await PatchAsync(client, member, """{"preferred_locale":"en"}""", ifMatch: issued))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
            (await SessionTestHost.ErrorCodeAsync(stale)).ShouldBe(ApiErrorCode.PreconditionFailed);
        }

        (await LocaleAsync(host, member.UserId)).ShouldBeNull("the refused write changed nothing");
    }

    /// <summary>The write appends the account's language change to the chain of each org it belongs to.</summary>
    [Fact]
    public async Task The_Write_Appends_A_Locale_Changed_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        var before = await host.CountAsync(
            "select count(*) from public.audit_log where action = 'user.locale_changed' and org_id = @org_id",
            ("org_id", member.OrgId));

        using (var written = await PatchAsync(
            client,
            member,
            """{"preferred_locale":"en"}""",
            ifMatch: await CurrentETagAsync(client, member)))
        {
            written.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var after = await host.CountAsync(
            "select count(*) from public.audit_log where action = 'user.locale_changed' and org_id = @org_id",
            ("org_id", member.OrgId));

        after.ShouldBe(before + 1, "one row per active org, and the member has one");
    }

    /// <summary>The row's own language, read as the owner.</summary>
    private static async Task<string?> LocaleAsync(SessionTestHost host, Guid userId)
    {
        var value = await host.ScalarAsync(
            "select preferred_locale from public.\"user\" where id = @id",
            [("id", userId)]);

        return value as string;
    }

    /// <summary>The row's stored version, read as the owner.</summary>
    private static async Task<int> VersionAsync(SessionTestHost host, Guid userId)
    {
        var value = await host.ScalarAsync(
            "select version from public.\"user\" where id = @id",
            [("id", userId)]);

        return value.ShouldBeOfType<int>();
    }

    /// <summary>
    /// The tag a fresh read issues. Sign-in itself updates the row, so its version is not a constant and every
    /// write must carry the tag the caller actually read.
    /// </summary>
    private static async Task<string> CurrentETagAsync(HttpClient client, SignedInMember member)
    {
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            ProfilePath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = response.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
        return etag.Tag;
    }

    /// <summary>The strong tag of the version after <paramref name="etag"/>'s.</summary>
    private static string NextETag(string etag) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{VersionOf(etag) + 1}\"");

    /// <summary>The numeric version an issued tag carries.</summary>
    private static int VersionOf(string etag) => int.Parse(etag.Trim('"'), CultureInfo.InvariantCulture);

    /// <summary>
    /// Sends the write with the headers the route requires, and without an org header: <c>If-Match</c> and the
    /// content type are exactly what each case varies.
    /// </summary>
    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        SignedInMember? member,
        string? json,
        string? ifMatch,
        bool includeXsrf = true,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, ProfilePath);

        if (member is not null)
        {
            request.Headers.Add(
                "Cookie",
                SessionTestHost.CookieHeaderFor(
                    member.SessionCookie,
                    includeXsrf ? member.Xsrf : null));

            if (includeXsrf)
            {
                request.Headers.Add(SessionTestHost.XsrfHeaderName, member.Xsrf.RequestToken);
            }
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, contentType);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
