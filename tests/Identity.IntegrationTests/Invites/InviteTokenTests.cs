using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// The invite token itself (02.1.2-T2 §5, T75): its shape and entropy, the HMAC store/verify pair
/// and the secret carrier the routes answer from. The unit cases need neither database nor host:
/// they are the seam's own contract. The two persistence cases at the end drive the real route over
/// the harness's migrated database and scan what the mint actually left behind (00 §5.7 S3);
/// the ids-only payload of the notification is pinned by
/// <c>InviteLifecycleTests.Outbox_OneNotifyRowPerMintWithIdsOnly</c> instead.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class InviteTokenTests : CatalogueTestBase
{
    public InviteTokenTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>A pepper of the documented shape (32 bytes), built at runtime so no literal key exists (00 §5.7 S3).</summary>
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    /// <summary>The version every hash below is made under.</summary>
    private const int PepperVersion = 3;

    [Fact]
    public void Token_CarriesTheSchemePrefixAndA256BitSecret()
    {
        var token = InviteTokenFormat.Generate();

        Assert.Equal(InviteTokenFormat.PrefixLength, token.Prefix.Length);
        Assert.All(token.Prefix, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
        Assert.Equal($"{InviteTokenFormat.Scheme}_{token.Prefix}_{token.Secret}", token.Full);
        Assert.True(InviteTokenFormat.TryParse(token.Full, out var prefix, out var secret));
        Assert.Equal(token.Prefix, prefix);

        // 256 bits of secret: base64url of 32 bytes, and every byte is recoverable from it.
        var bytes = DecodeBase64Url(secret);
        Assert.Equal(32, bytes.Length);
        Assert.Equal(token.Secret, secret);
    }

    [Fact]
    public void Tokens_NeverShareAPrefix()
    {
        // 62^12 prefixes make a collision astronomically unlikely; a duplicate here means the
        // generator repeats (a seeded or unrandomised draw), which the unique index would refuse.
        var prefixes = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 1_000; index++)
        {
            Assert.True(prefixes.Add(InviteTokenFormat.Generate().Prefix));
        }
    }

    [Fact]
    public void TryParse_RejectsAnythingButTheExactShape()
    {
        var token = InviteTokenFormat.Generate();

        Assert.False(InviteTokenFormat.TryParse(null, out _, out _));
        Assert.False(InviteTokenFormat.TryParse(string.Empty, out _, out _));
        Assert.False(InviteTokenFormat.TryParse($"{token.Full}x", out _, out _));
        Assert.False(InviteTokenFormat.TryParse(token.Full.Replace('_', '-'), out _, out _));
        Assert.False(InviteTokenFormat.TryParse($"evgi_{token.Prefix}", out _, out _));

        // The bound of 00 §5.7 S3: a token longer than the contract is refused without a lookup.
        Assert.False(InviteTokenFormat.TryParse(
            $"{InviteTokenFormat.Scheme}_{token.Prefix}_{new string('a', InviteTokenFormat.MaxLength)}",
            out _,
            out _));
    }

    [Fact]
    public void Hash_And_Verify_RoundTrip_UnderTheConfiguredVersion()
    {
        var secrets = Secrets(Pepper, PepperVersion);
        var token = InviteTokenFormat.Generate();

        Assert.True(secrets.CanHash);

        var (version, hash) = secrets.HashCurrent(Encoding.UTF8.GetBytes(token.Secret));

        Assert.Equal(PepperVersion, version);
        Assert.Equal(32, hash.Length);
        Assert.True(secrets.Verify(version, hash, token.Secret));
    }

    [Fact]
    public void Verify_RefusesAnotherSecretAnotherVersionOrNoPepper()
    {
        var secrets = Secrets(Pepper, PepperVersion);
        var token = InviteTokenFormat.Generate();
        var (version, hash) = secrets.HashCurrent(Encoding.UTF8.GetBytes(token.Secret));

        Assert.False(secrets.Verify(version, hash, InviteTokenFormat.Generate().Secret));
        Assert.False(secrets.Verify(version + 1, hash, token.Secret));

        // A deployment without the pepper cannot mint or verify anything (503 unavailable).
        var unconfigured = Secrets(pepper: null, PepperVersion);

        Assert.False(unconfigured.CanHash);
        Assert.False(unconfigured.Verify(version, hash, token.Secret));
        Assert.Throws<InvalidOperationException>(
            () => unconfigured.HashCurrent(Encoding.UTF8.GetBytes(token.Secret)));
    }

    [Fact]
    public async Task SecretCarrier_HandsTheTokenBackAcrossAnAwaitedHandlerAndOnceOnly()
    {
        var carrier = new InviteSecretCarrier();
        var token = InviteTokenFormat.Generate().Full;

        using (carrier.BeginRequest())
        {
            // The shape the endpoint relies on: the slot opens first, and a write made after the
            // handler's own awaits is what the endpoint then reads.
            await MintsAsync(carrier, token);

            Assert.Equal(token, carrier.Token);
            Assert.Equal(token, carrier.Take());

            // Given out once: a second read finds nothing, so a replay path yields no token.
            Assert.Null(carrier.Token);
        }

        // The enclosing slot is restored, so two requests on one flow never share a token.
        Assert.Null(carrier.Token);
    }

    /// <summary>
    /// §8 T75 / 00 §5.7 S3: the minted token is shown once and never stored. The membership row
    /// keeps the public prefix, the 32-byte HMAC of the secret half and its pepper version — and
    /// the three places a careless implementation would leak it (the notification outbox row, the
    /// audit entry, the <c>Idempotency-Key</c> response store) hold neither half of it.
    /// </summary>
    [Fact]
    public async Task PlaintextNeverPersisted()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            "plaintext-1");

        created.Status.ShouldBe(HttpStatusCode.Created);

        var membershipId = created.Body.GetProperty("id").GetGuid();
        var token = created.Body.GetProperty("invite_token").GetString();
        string.IsNullOrWhiteSpace(token).ShouldBeFalse("the response that mints the invite is the only place the plaintext appears");
        InviteTokenFormat.TryParse(token, out var prefix, out var secret).ShouldBeTrue();
        var fullToken = token!;

        // The row at rest: no half of the token, only what the resolver and the verifier need.
        var row = await TextAsync(
            "select to_jsonb(m)::text from public.membership m where m.id = @id",
            ("id", membershipId));
        row.Contains(secret, StringComparison.Ordinal).ShouldBeFalse("the secret half must never be stored");
        row.Contains(fullToken, StringComparison.Ordinal).ShouldBeFalse("the full token must never be stored");

        (await TextAsync("select invite_secret_prefix from public.membership where id = @id", ("id", membershipId)))
            .ShouldBe(prefix);

        var storedVersion = await ScalarAsync<int>(
            "select invite_pepper_version from public.membership where id = @id",
            ("id", membershipId));
        var storedHash = await ScalarAsync<byte[]>(
            "select invite_token_hash from public.membership where id = @id",
            ("id", membershipId));
        storedHash.Length.ShouldBe(32, "the stored digest is an HMAC-SHA256 of the secret half");

        // Byte-exact: HMAC-SHA256 of the SECRET half under the pepper this host was started with.
        var secrets = new InviteSecrets(factory.Services.GetRequiredService<IOptions<InviteOptions>>());
        secrets.CanHash.ShouldBeTrue("the host is started with a pepper; without one the create answers 503");
        var (currentVersion, currentHash) = secrets.HashCurrent(Encoding.UTF8.GetBytes(secret));
        storedVersion.ShouldBe(currentVersion);
        storedHash.ShouldBe(currentHash);
        secrets.Verify(storedVersion, storedHash, secret).ShouldBeTrue("the product seam agrees with the bytes at rest");

        // The three carriers that must stay secret-free, scanned as text (jsonb included).
        var scan = new (string Table, string Sql)[]
        {
            ("public.outbox", "select coalesce(string_agg(to_jsonb(o)::text, chr(10)), '') from public.outbox o where o.org_id = @org"),
            ("public.audit_log", "select coalesce(string_agg(to_jsonb(a)::text, chr(10)), '') from public.audit_log a where a.org_id = @org"),
            ("public.idempotency_record", "select coalesce(string_agg(to_jsonb(r)::text, chr(10)), '') from public.idempotency_record r where r.org_id = @org"),
        };

        foreach (var (table, sql) in scan)
        {
            var text = await TextAsync(sql, ("org", org));

            text.Contains(secret, StringComparison.Ordinal).ShouldBeFalse($"{table} must not hold the invite secret");
            text.Contains(fullToken, StringComparison.Ordinal).ShouldBeFalse($"{table} must not hold the full invite token");
            text.Contains(prefix, StringComparison.Ordinal).ShouldBeFalse($"{table} must not hold the invite prefix");
        }

        // The scan is not vacuous: the create did write into all three.
        (await ScalarAsync<long>("select count(*) from public.outbox where org_id = @org", ("org", org))).ShouldBe(1);
        (await ScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = 'membership.invite'",
            ("org", org))).ShouldBe(1);
        (await ScalarAsync<long>("select count(*) from public.idempotency_record where org_id = @org", ("org", org))).ShouldBe(1);
    }

    /// <summary>
    /// [DECISION D-S78-Q1]: the minted token travels outside the command's <c>Result</c>, so the
    /// response store never holds it. Replaying the same <c>Idempotency-Key</c> answers the same
    /// 201 without a token and writes nothing a second time — rotating is what <c>resend</c> is for.
    /// </summary>
    [Fact]
    public async Task IdempotencyStoreHoldsNoSecret_ReplayOmitsToken()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        const string Body = """{"email":"alice@example.com"}""";

        var create = await PostAsync(client, $"/v1/orgs/{org}/memberships", Body, "invite-idem-1");
        create.Status.ShouldBe(HttpStatusCode.Created);

        var membershipId = create.Body.GetProperty("id").GetGuid();
        var token = create.Body.GetProperty("invite_token").GetString();
        string.IsNullOrWhiteSpace(token).ShouldBeFalse();
        InviteTokenFormat.TryParse(token, out _, out var secret).ShouldBeTrue();
        var fullToken = token!;

        var replay = await PostAsync(client, $"/v1/orgs/{org}/memberships", Body, "invite-idem-1");

        replay.Status.ShouldBe(HttpStatusCode.Created, "the stored 201 is replayed instead of running the handler again");
        replay.Body.GetProperty("id").GetGuid().ShouldBe(membershipId);
        AssertNoToken(replay.Body);

        // Nothing was written a second time: one row, one notification, one store record.
        (await ScalarAsync<long>("select count(*) from public.membership where org_id = @org", ("org", org))).ShouldBe(1);
        (await ScalarAsync<long>(
            "select count(*) from public.outbox where org_id = @org and task_type = 'identity.invite.notify'",
            ("org", org))).ShouldBe(1);
        (await ScalarAsync<long>(
            "select count(*) from public.idempotency_record where org_id = @org and key = @key",
            ("org", org),
            ("key", "invite-idem-1"))).ShouldBe(1);

        var stored = await TextAsync(
            "select response_body::text from public.idempotency_record where org_id = @org and key = @key",
            ("org", org),
            ("key", "invite-idem-1"));

        string.IsNullOrEmpty(stored).ShouldBeFalse("the create completed a store record for its Idempotency-Key");
        stored.Contains(secret, StringComparison.Ordinal).ShouldBeFalse("the store must not hold the invite secret");
        stored.Contains(fullToken, StringComparison.Ordinal).ShouldBeFalse("the store must not hold the full invite token");

        // The wire's one-time field is not part of the stored body at all; the command's own result
        // (which the store persists) can only ever carry a null token.
        stored.Contains("invite_token", StringComparison.Ordinal).ShouldBeFalse("the store keeps the secret-free command result, never the body the endpoint completed");

        using var document = JsonDocument.Parse(stored);
        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("InviteToken", StringComparison.OrdinalIgnoreCase))
            {
                property.Value.ValueKind.ShouldBe(JsonValueKind.Null, "the stored result never carries a token");
            }
        }
    }

    /// <summary>Asserts a response body carries no usable token: absent, or an explicit null.</summary>
    private static void AssertNoToken(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("invite_token", out var token))
        {
            token.ValueKind.ShouldBe(JsonValueKind.Null, "an Idempotency-Key replay answers the stored 201, which never carried a token");
        }
    }

    /// <summary>One owner-connection statement: the harness's arranging/asserting seam (00 §5.5 C3).</summary>
    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>The same seam for a text column or an aggregate, where NULL reads as the empty string.</summary>
    private async Task<string> TextAsync(string sql, params (string Name, object Value)[] parameters) =>
        await ScalarAsync<string?>(sql, parameters) ?? string.Empty;

    /// <summary>One JSON POST through the real Api composition, with its <c>Idempotency-Key</c>.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client,
        string path,
        string? json,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);

        return (response.StatusCode, body);
    }

    private static async Task MintsAsync(InviteSecretCarrier carrier, string token)
    {
        await Task.Yield();
        carrier.Token = token;
    }

    private static InviteSecrets Secrets(byte[]? pepper, int version) =>
        new(Options.Create(new InviteOptions
        {
            Pepper = pepper is null ? null : Convert.ToBase64String(pepper),
            PepperVersion = version,
        }));

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);

        return Convert.FromBase64String(padded);
    }
}

/// <summary>Boot validation of the invite options: a pepper must be base64 of at least 32 bytes.</summary>
[Trait("Category", "Docker")]
public sealed class InviteOptionsValidatorTests
{
    private static readonly InviteOptionsValidator Validator = new();

    [Fact]
    public void AbsentPepperIsAllowedAtBoot()
    {
        // Minting stays off (503 at the routes) instead of failing the host, exactly like the
        // session and CI key material.
        Assert.True(Validator.Validate(null, new InviteOptions()).Succeeded);
        Assert.True(Validator.Validate(null, new InviteOptions { Pepper = "   " }).Succeeded);
    }

    [Fact]
    public void ShortOrMalformedPepperIsRefusedByNameOnly()
    {
        var shortPepper = Validator.Validate(
            null,
            new InviteOptions { Pepper = Convert.ToBase64String(new byte[InviteSecrets.MinKeyBytes - 1]) });
        var malformed = Validator.Validate(null, new InviteOptions { Pepper = "not base64 !!" });
        var version = Validator.Validate(null, new InviteOptions { PepperVersion = 0 });

        Assert.True(shortPepper.Failed);
        Assert.True(malformed.Failed);
        Assert.True(version.Failed);

        // The message names the key and never a value (the pepper travels through this object).
        Assert.All(
            shortPepper.Failures ?? [],
            failure => Assert.True(failure.Contains("EVIDGATE:Invite:Pepper", StringComparison.Ordinal)));
        Assert.All(
            malformed.Failures ?? [],
            failure => Assert.True(failure.Contains("EVIDGATE:Invite:Pepper", StringComparison.Ordinal)));
    }
}
