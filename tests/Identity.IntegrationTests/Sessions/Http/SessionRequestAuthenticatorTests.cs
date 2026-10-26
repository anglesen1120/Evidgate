using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// The request authenticator's own decisions, without a database (R1/SEC-1, R2/SEC-2, P2 of the 02.3.2-T1 review):
/// a privilege-marked session proves the antiforgery pair BEFORE anything rotates, a failed rotation is mapped to
/// the right answer per cause, and the session proof is derived only when a handler asks for it.
/// </summary>
/// <remarks>
/// <para>
/// The two seams the class substitutes are its collaborators, not its logic: <see cref="ISessionAuthentication"/>
/// (already judged outside) and <see cref="ISessions"/> (the rotation writer). Everything the cases assert on is
/// this class's own behaviour: whether the rotation was called at all, what the outcome says, and whether a
/// <c>Set-Cookie</c> reached the response.
/// </para>
/// <para>
/// No database is touched, but the class carries the Docker trait like every other class of this project: the
/// assembly fixture needs the container whether or not one test of the class reaches it.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRequestAuthenticatorTests
{
    /// <summary>A value shaped exactly like one the store mints (43 base64url characters).</summary>
    private const string PresentedCookie = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>The value a rotation would issue: also a minted shape, and never the presented one.</summary>
    private const string RotatedCookie = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static readonly Guid SessionId = Guid.CreateVersion7();
    private static readonly Guid RotatedSessionId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(index => (byte)(index + 1)).ToArray();

    /// <summary>
    /// R1/SEC-1: a mutation on a rotation-required session that cannot prove the pair answers 403
    /// <c>csrf_failed</c> and NOTHING else happens — no <c>session_rotate</c>, no <c>Set-Cookie</c>, no live
    /// outcome. A forged cross-site mutation therefore cannot burn the session it forged with.
    /// </summary>
    [Fact]
    public async Task A_Mutation_That_Cannot_Prove_The_Pair_Never_Rotates()
    {
        var sessions = new RecordingSessions(Rotated());
        var authenticator = Build(new FixedAuthentication(RotationRequired()), sessions, new FixedAntiforgery(valid: false));
        var http = Request(HttpMethods.Post);

        var outcome = await authenticator.AuthenticateAsync(http, TestContext.Current.CancellationToken);

        var refusal = outcome.Refusal;
        refusal.ShouldNotBeNull("a failed pair is a refusal");
        refusal!.Status.ShouldBe(StatusCodes.Status403Forbidden);
        refusal.Code.ShouldBe(ApiErrorCode.CsrfFailed);
        outcome.UserId.ShouldBeNull("a refused request has no caller");
        outcome.SessionId.ShouldBeNull("no successor row was named");
        outcome.GetSessionProof().ShouldBeNull();

        sessions.Rotations.ShouldBe(0, "the rotation must not run before the pair is proved");
        http.Response.Headers.SetCookie.ShouldBeEmpty("a refused rotation issues no cookie at all");
    }

    /// <summary>
    /// R1 (the allowed half) and P2: a mutation that DOES prove the pair rotates once, issues the successor's
    /// cookie, and hands out the hash of the NEW cookie value — computed on demand and memoized.
    /// </summary>
    [Fact]
    public async Task A_Mutation_With_The_Pair_Rotates_Once_And_Proves_The_New_Cookie()
    {
        var sessions = new RecordingSessions(Rotated());
        var authenticator = Build(new FixedAuthentication(RotationRequired()), sessions, new FixedAntiforgery(valid: true));
        var http = Request(HttpMethods.Delete);

        var outcome = await authenticator.AuthenticateAsync(http, TestContext.Current.CancellationToken);

        outcome.Refusal.ShouldBeNull();
        outcome.UserId.ShouldBe(UserId);
        outcome.SessionId.ShouldBe(RotatedSessionId, "the outcome names the session the request continues on");
        sessions.Rotations.ShouldBe(1, "one request, one rotation");

        http.Response.Headers.SetCookie.ShouldContain(
            header => header != null
                && header.StartsWith(SessionCookieNames.Session + "=" + RotatedCookie, StringComparison.Ordinal),
            "the successor's cookie is issued in the same response");

        var proof = outcome.GetSessionProof();
        proof.ShouldNotBeNull();
        proof!.ShouldBe(HmacOf(RotatedCookie));
        proof.ShouldNotBe(HmacOf(PresentedCookie));
        outcome.GetSessionProof().ShouldBeSameAs(proof, "the proof is derived once per request, not once per caller");
    }

    /// <summary>
    /// R1 boundary: a SAFE method is outside the antiforgery rule (there is no state to forge), so a
    /// rotation-required read still rotates without proving a pair — the rule never makes a GET fail.
    /// </summary>
    [Fact]
    public async Task A_Read_That_Needs_Rotation_Rotates_Without_A_Pair()
    {
        var sessions = new RecordingSessions(Rotated());
        var authenticator = Build(new FixedAuthentication(RotationRequired()), sessions, new FixedAntiforgery(valid: false));
        var http = Request(HttpMethods.Get);

        var outcome = await authenticator.AuthenticateAsync(http, TestContext.Current.CancellationToken);

        outcome.Refusal.ShouldBeNull("a safe method is not in antiforgery scope");
        sessions.Rotations.ShouldBe(1);
        outcome.SessionId.ShouldBe(RotatedSessionId);
    }

    /// <summary>
    /// R2: a rotation the store answers as <c>account_disabled</c> is an AUTHORIZATION answer (403), not a
    /// credential one — the browser must not drop a session over a disabled account.
    /// </summary>
    [Fact]
    public async Task A_Disabled_Account_On_Rotation_Answers_403_Account_Disabled()
    {
        var sessions = new RecordingSessions(Result.Failure<SessionTicket>(SessionErrors.AccountDisabled));
        var authenticator = Build(new FixedAuthentication(RotationRequired()), sessions, new FixedAntiforgery(valid: true));

        var outcome = await authenticator.AuthenticateAsync(
            Request(HttpMethods.Post),
            TestContext.Current.CancellationToken);

        var refusal = outcome.Refusal;
        refusal.ShouldNotBeNull();
        refusal!.Status.ShouldBe(StatusCodes.Status403Forbidden);
        refusal.Code.ShouldBe(ApiErrorCode.AccountDisabled);
    }

    /// <summary>R2 boundary: a session that is not live is the only cause mapped to 401 <c>session_revoked</c>.</summary>
    [Fact]
    public async Task A_Session_That_Is_Not_Live_On_Rotation_Answers_401_Session_Revoked()
    {
        var sessions = new RecordingSessions(Result.Failure<SessionTicket>(SessionErrors.SessionRevoked));
        var authenticator = Build(new FixedAuthentication(RotationRequired()), sessions, new FixedAntiforgery(valid: true));

        var outcome = await authenticator.AuthenticateAsync(
            Request(HttpMethods.Post),
            TestContext.Current.CancellationToken);

        var refusal = outcome.Refusal;
        refusal.ShouldNotBeNull();
        refusal!.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        refusal.Code.ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>R2 boundary: an outage keeps its 503 — it never signs a user out of a session nobody judged.</summary>
    [Fact]
    public async Task An_Outage_On_Rotation_Keeps_503_Unavailable()
    {
        var sessions = new RecordingSessions(Result.Failure<SessionTicket>(SessionErrors.Unavailable));
        var authenticator = Build(new FixedAuthentication(RotationRequired()), sessions, new FixedAntiforgery(valid: true));

        var outcome = await authenticator.AuthenticateAsync(
            Request(HttpMethods.Post),
            TestContext.Current.CancellationToken);

        var refusal = outcome.Refusal;
        refusal.ShouldNotBeNull();
        refusal!.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        refusal.Code.ShouldBe(ApiErrorCode.Unavailable);
    }

    /// <summary>
    /// P2: a request that did not rotate proves the PRESENTED value, and only when it is asked — the common path
    /// (every authenticated read) pays no second HMAC for a handler that is not the link confirm.
    /// </summary>
    [Fact]
    public async Task A_Request_That_Did_Not_Rotate_Proves_The_Presented_Value()
    {
        var sessions = new RecordingSessions(Rotated());
        var authenticator = Build(new FixedAuthentication(Active()), sessions, new FixedAntiforgery(valid: false));

        var outcome = await authenticator.AuthenticateAsync(
            Request(HttpMethods.Get),
            TestContext.Current.CancellationToken);

        outcome.Refusal.ShouldBeNull();
        outcome.SessionId.ShouldBe(SessionId);
        sessions.Rotations.ShouldBe(0);

        outcome.GetSessionProof().ShouldBe(HmacOf(PresentedCookie));
    }

    // ------------------------------------------------------------------ the seams and helpers

    /// <summary>
    /// HMAC-SHA256 of a cookie value under the class's known pepper. A plain helper rather than an inline call:
    /// these cases are <c>async</c>, and the synchronous <c>HashData</c> overload inside an async method is a
    /// VSTHRD103 build error (the same shape as <c>SessionHardeningTestBase.HmacOf</c>).
    /// </summary>
    /// <param name="cookieValue">The presented or issued cookie value.</param>
    /// <returns>The proof the store would keep for that value.</returns>
    private static byte[] HmacOf(string cookieValue) =>
        HMACSHA256.HashData(Pepper, Encoding.ASCII.GetBytes(cookieValue));

    /// <summary>Builds the class under test over the case's collaborators and the known pepper.</summary>
    private static SessionRequestAuthenticator Build(
        ISessionAuthentication authentication,
        ISessions sessions,
        IAntiforgery antiforgery) =>
        new(
            authentication,
            sessions,
            new SessionSecrets(Options.Create(new SessionOptions
            {
                Pepper = Convert.ToBase64String(Pepper),
                PepperVersion = 1,
            })),
            antiforgery,
            TimeProvider.System);

    /// <summary>A request carrying the suite's presented cookie, with no antiforgery header (the case's fake decides).</summary>
    private static DefaultHttpContext Request(string method)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Headers.Cookie = SessionCookieNames.Session + "=" + PresentedCookie;

        return http;
    }

    /// <summary>A live session the authenticator answers active.</summary>
    private static SessionAuthResult Active() => new(SessionAuthStatus.Active, SessionId, UserId, RotationRequired: false);

    /// <summary>A live session a privilege change flagged, which is what makes the next request rotate.</summary>
    private static SessionAuthResult RotationRequired() => new(SessionAuthStatus.Active, SessionId, UserId, RotationRequired: true);

    /// <summary>The ticket a successful rotation hands back.</summary>
    private static Result<SessionTicket> Rotated() =>
        Result.Success(new SessionTicket(
            RotatedSessionId,
            RotatedCookie,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddHours(2)));

    /// <summary>The lookup half, already judged: it answers the case's one result and never touches a database.</summary>
    private sealed class FixedAuthentication(SessionAuthResult result) : ISessionAuthentication
    {
        /// <inheritdoc />
        public ValueTask<Result<SessionAuthResult>> AuthenticateAsync(
            string? cookieValue,
            CancellationToken cancellationToken) =>
            new(Result.Success(result));
    }

    /// <summary>The rotation writer, which counts the rotations it was asked for and answers the case's result.</summary>
    private sealed class RecordingSessions(Result<SessionTicket> rotation) : ISessions
    {
        /// <summary>How many rotations this request asked for: one is the rule, zero is R1's refusal.</summary>
        public int Rotations { get; private set; }

        /// <inheritdoc />
        public ValueTask<Result<SessionTicket>> CreateAsync(
            SessionCreation request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the request path never creates a session");

        /// <inheritdoc />
        public ValueTask<Result<SessionTicket>> RotateAsync(
            SessionRotation request,
            CancellationToken cancellationToken)
        {
            Rotations++;

            return new(rotation);
        }
    }

    /// <summary>The antiforgery service with one fixed verdict; a rotation's refreshed pair is not minted here.</summary>
    private sealed class FixedAntiforgery(bool valid) : IAntiforgery
    {
        /// <summary>
        /// The set this fake mints: a real instance with NO request token. <see cref="SessionAntiforgery"/> reads
        /// <c>RequestToken</c> off the result before anything else, so a null set would throw there; an empty
        /// request token makes the writer's own guard skip the XSRF cookies, and a case that only cares about the
        /// session cookie is not forced to mint a pair it would have to validate with.
        /// </summary>
        private static readonly AntiforgeryTokenSet NoRequestToken =
            new(requestToken: null, cookieToken: null, formFieldName: "__RequestVerificationToken", headerName: "RequestVerificationToken");

        /// <inheritdoc />
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => NoRequestToken;

        /// <inheritdoc />
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => NoRequestToken;

        /// <inheritdoc />
        public Task<bool> IsRequestValidAsync(HttpContext context) => Task.FromResult(valid);

        /// <inheritdoc />
        public Task ValidateRequestAsync(HttpContext context) =>
            valid ? Task.CompletedTask : Task.FromException(new InvalidOperationException("the pair is invalid"));

        /// <inheritdoc />
        public void SetCookieTokenAndHeader(HttpContext context)
        {
            // Nothing to set: this fake never mints.
        }
    }
}
