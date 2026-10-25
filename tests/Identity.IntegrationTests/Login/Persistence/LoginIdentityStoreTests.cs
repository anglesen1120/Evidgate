using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Persistence;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// 02.3.1-T1.4 / 02.3.1 §5: <c>NpgsqlLoginIdentityStore</c> against the real server — the first login, the
/// repeat login, the two-providers-one-address case of D3/AC6, a concurrent first login of one identity, a
/// disabled account and an unknown provider.
/// </summary>
/// <remarks>
/// The store's only writer is <c>evidgate_resolve.login_upsert_identity</c> (migration 0042), so every
/// assertion reads what the owner connection sees afterwards: this is the row shape the login command hands
/// to the session store, not a memento of the SQL.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class LoginIdentityStoreTests : SessionTestBase
{
    private readonly NpgsqlLoginIdentityStore _store;

    public LoginIdentityStoreTests(DatabaseFixture fixture)
        : base(fixture) => _store = new NpgsqlLoginIdentityStore(AppDataSource);

    /// <summary>AC1's first half: a verified identity creates one <c>user</c> and one <c>user_identity</c>.</summary>
    [Fact]
    public async Task First_Login_Creates_User_And_Identity()
    {
        var login = await _store.UpsertAsync(
            Identity("github", "1024", "octocat@example.com", "The Octocat"),
            Token);

        login.IsActive.ShouldBeTrue();
        login.UserId.ShouldNotBe(Guid.Empty);

        var user = await OwnerRowAsync(
            "select email, status, display_name, last_login_at, updated_at from public.\"user\" where id = @id",
            ("id", login.UserId));
        user[0].ShouldBe("octocat@example.com");
        user[1].ShouldBe("active");
        user[2].ShouldBe("The Octocat");
        user[3].ShouldNotBeNull("a completed login stamps last_login_at");
        user[4].ShouldNotBeNull();

        var identity = await OwnerRowAsync(
            "select user_id, provider, subject, email_at_provider, linked_at "
            + "from public.user_identity where user_id = @user_id",
            ("user_id", login.UserId));
        identity[0].ShouldBe(login.UserId);
        identity[1].ShouldBe("github");
        identity[2].ShouldBe("1024");
        identity[3].ShouldBe("octocat@example.com");
        identity[4].ShouldNotBeNull();
    }

    /// <summary>
    /// FR-02-4: a second login of the same <c>(provider, subject)</c> is the same human. It refreshes
    /// <c>last_login_at</c>, the address the provider reported and (only when one is given) the display
    /// name — and it never rewrites <c>user.email</c>, which is the address the account was created with.
    /// </summary>
    [Fact]
    public async Task Repeat_Login_Returns_The_Same_User_And_Never_Rewrites_The_Email()
    {
        var first = await _store.UpsertAsync(
            Identity("github", "2048", "original@example.com", "First Name"),
            Token);

        // Arrange a last_login_at far in the past, so the refresh is observable without a clock race.
        await OwnerExecuteAsync(
            "update public.\"user\" set last_login_at = timestamptz '2020-01-01 00:00:00+00' where id = @id",
            ("id", first.UserId));

        var second = await _store.UpsertAsync(
            Identity("github", "2048", "changed@example.com", "Second Name"),
            Token);

        second.UserId.ShouldBe(first.UserId);
        second.IsActive.ShouldBeTrue();

        var user = await OwnerRowAsync(
            "select email, display_name, last_login_at > timestamptz '2020-01-02 00:00:00+00' "
            + "from public.\"user\" where id = @id",
            ("id", first.UserId));
        user[0].ShouldBe("original@example.com", "a login never rewrites the address the account was created with");
        user[1].ShouldBe("Second Name");
        user[2].ShouldBe(true, "a repeat login refreshes last_login_at");

        (await OwnerScalarAsync<string>(
            "select email_at_provider from public.user_identity where user_id = @user_id",
            ("user_id", first.UserId))).ShouldBe("changed@example.com");

        // A provider that stops sending a name must not erase the one we have.
        var third = await _store.UpsertAsync(
            Identity("github", "2048", "changed@example.com", null),
            Token);
        third.UserId.ShouldBe(first.UserId);

        (await OwnerScalarAsync<string>(
            "select display_name from public.\"user\" where id = @id",
            ("id", first.UserId))).ShouldBe("Second Name");

        (await OwnerScalarAsync<long>(
            "select count(*) from public.\"user\" where lower(email) in ('original@example.com', 'changed@example.com')"))
            .ShouldBe(1, "a repeat login creates no second user");
    }

    /// <summary>
    /// D3/AC6: the same address at two providers is two identities and two users. Email is never an identity
    /// key, and nothing merges automatically — the merge is an explicit 09 flow.
    /// </summary>
    [Fact]
    public async Task Two_Providers_With_The_Same_Email_Are_Two_Users()
    {
        var github = await _store.UpsertAsync(
            Identity("github", "3001", "shared@example.com", "Shared"),
            Token);
        var google = await _store.UpsertAsync(
            Identity("google", "sub-3001", "shared@example.com", "Shared"),
            Token);

        github.UserId.ShouldNotBe(google.UserId);

        (await OwnerScalarAsync<long>(
            "select count(*) from public.\"user\" where lower(email) = 'shared@example.com'")).ShouldBe(2);
        (await OwnerScalarAsync<long>(
            "select count(*) from public.user_identity where user_id = any(@ids)",
            ("ids", new[] { github.UserId, google.UserId }))).ShouldBe(2);
    }

    /// <summary>
    /// The race the unique key has to settle: sixteen concurrent first logins of ONE identity must leave
    /// exactly one user and one identity, every caller must be handed that same user, and the losing callers
    /// must not leave the user rows they created behind.
    /// </summary>
    [Fact]
    public async Task Concurrent_First_Logins_Create_One_User_And_One_Identity()
    {
        var identity = Identity("google", "sub-race", "race@example.com", "Race");

        var logins = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => _store.UpsertAsync(identity, Token).AsTask()));

        var userId = logins[0].UserId;
        logins.ShouldAllBe(login => login.UserId == userId);
        logins.ShouldAllBe(login => login.IsActive);

        (await OwnerScalarAsync<long>(
            "select count(*) from public.user_identity where provider = 'google' and subject = 'sub-race'"))
            .ShouldBe(1);
        (await OwnerScalarAsync<long>(
            "select count(*) from public.\"user\" where lower(email) = 'race@example.com'")).ShouldBe(1);
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from public."user" u
             where not exists (select 1 from public.user_identity ui where ui.user_id = u.id)
            """)).ShouldBe(0, "a losing first login must delete the user row it created");
    }

    /// <summary>
    /// 02.3.1 §6: a disabled account is reported, not refreshed. The caller turns
    /// <c>IsActive == false</c> into 403 <c>account_disabled</c> and issues no session.
    /// </summary>
    [Fact]
    public async Task Disabled_User_Is_Reported_And_Not_Refreshed()
    {
        var created = await _store.UpsertAsync(
            Identity("github", "4001", "disabled@example.com", "Disabled"),
            Token);

        await OwnerExecuteAsync(
            "update public.\"user\" set status = 'disabled', last_login_at = timestamptz '2020-01-01 00:00:00+00' "
            + "where id = @id",
            ("id", created.UserId));

        var login = await _store.UpsertAsync(
            Identity("github", "4001", "other@example.com", "Renamed"),
            Token);

        login.UserId.ShouldBe(created.UserId);
        login.IsActive.ShouldBeFalse();

        var user = await OwnerRowAsync(
            "select email, display_name, "
            + "last_login_at = timestamptz '2020-01-01 00:00:00+00' from public.\"user\" where id = @id",
            ("id", created.UserId));
        user[0].ShouldBe("disabled@example.com");
        user[1].ShouldBe("Disabled", "a refused login refreshes nothing");
        user[2].ShouldBe(true, "a refused login leaves last_login_at alone");

        (await OwnerScalarAsync<string>(
            "select email_at_provider from public.user_identity where user_id = @user_id",
            ("user_id", created.UserId))).ShouldBe("disabled@example.com");
    }

    /// <summary>
    /// T6: only the two providers of V1 exist, and the server — not a list in the handler — is what refuses
    /// an unknown one. The whole call rolls back, so no half-created account survives it.
    /// </summary>
    [Fact]
    public async Task Invalid_Provider_Is_Rejected_And_Leaves_No_User()
    {
        var refusal = await Should.ThrowAsync<PostgresException>(
            () => _store.UpsertAsync(
                Identity("saml", "5001", "saml@example.com", "Saml"),
                Token).AsTask());

        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("user_identity_provider_check");

        (await OwnerScalarAsync<long>(
            "select count(*) from public.\"user\" where lower(email) = 'saml@example.com'")).ShouldBe(0);
    }

    /// <summary>
    /// A provider `name` is unbounded while <c>user.display_name</c> is 1..200 (0041): a long name is cut to
    /// the column, and an empty name becomes NULL rather than violating the check's lower bound.
    /// Neither may abort a login the provider already verified.
    /// </summary>
    [Fact]
    public async Task Long_Or_Blank_Provider_Name_Is_Bounded_Not_Rejected()
    {
        var longName = new string('N', 201);
        var longUser = await _store.UpsertAsync(
            Identity("github", "7001", "long@example.com", longName),
            Token);

        (await OwnerScalarAsync<string>(
            "select display_name from public.\"user\" where id = @id",
            ("id", longUser.UserId))).ShouldBe(new string('N', 200));

        var blankUser = await _store.UpsertAsync(
            Identity("github", "7002", "blank@example.com", string.Empty),
            Token);

        (await OwnerScalarAsync<bool>(
            "select display_name is null from public.\"user\" where id = @id",
            ("id", blankUser.UserId))).ShouldBeTrue();
    }

    /// <summary>A verified identity of one provider, as a provider handler hands it over.</summary>
    /// <param name="provider">Provider name.</param>
    /// <param name="subject">Provider subject.</param>
    /// <param name="email">Verified address.</param>
    /// <param name="displayName">Profile name, or null.</param>
    private static VerifiedIdentity Identity(string provider, string subject, string email, string? displayName) =>
        new(provider, subject, email, EmailVerified: true, DisplayName: displayName);
}
