using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Ingest-token cases of <see cref="CiAuthTests"/>.</summary>
public sealed partial class CiAuthTests
{
    // ---------------------------------------------------------------- ingest tokens

    [Fact]
    public async Task IngestToken_ShowsThePlaintextOnce_AndStoresOnlyPrefixAndHmac()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var created = await CreateTokenAsync(client, user, org, project);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var secret = created.Text("secret")!;
        secret.ShouldStartWith("egt_");
        var prefix = created.Body.GetProperty("token").GetProperty("prefix").GetString()!;
        secret.ShouldContain(prefix);

        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ingest-tokens", Admin(user, org));
        list.Body.GetRawText().ShouldNotContain(secret);
        list.Body.GetRawText().ShouldNotContain(secret[^43..]);
        list.Body.GetProperty("items")[0].GetProperty("status").GetString().ShouldBe("active");

        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.ingest_token where position('{secret[^43..]}' in name || prefix) > 0")).ShouldBe(0);
        (await _fixture.ScalarAsync<int>(_database!, "select octet_length(token_hmac) from public.ingest_token")).ShouldBe(32);
        (await _fixture.ScalarAsync<int>(_database!, "select pepper_version from public.ingest_token")).ShouldBe(2);
    }

    [Fact]
    public async Task IngestToken_Exchange_ReturnsAProducerScopedToTheProject_AndUniformFailures()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var secret = (await CreateTokenAsync(client, user, org, project)).Text("secret")!;

        var ok = await ExchangeIngestAsync(client, secret);
        ok.Status.ShouldBe(HttpStatusCode.OK);
        ok.Id("project_id").ShouldBe(project);
        new JsonWebToken(ok.Text("token")!).GetPayloadValue<string>("org_id").ShouldBe(org.ToString("D"));

        var wrongSecret = await ExchangeIngestAsync(client, secret[..^43] + new string('A', 43));
        var unknownPrefix = await ExchangeIngestAsync(client, "egt_ZZZZZZZZZZZZ_" + new string('A', 43));
        var malformed = await ExchangeIngestAsync(client, "hunter2");
        foreach (var answer in new[] { wrongSecret, unknownPrefix, malformed })
        {
            answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
            answer.ErrorCode.ShouldBe("token_unknown");
        }

        wrongSecret.ErrorMessage.ShouldBe(unknownPrefix.ErrorMessage);
    }

    [Fact]
    public async Task IngestToken_LastUsedAt_IsThrottled()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var secret = (await CreateTokenAsync(client, user, org, project)).Text("secret")!;

        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        var version = await _fixture.ScalarAsync<int>(_database!, "select version from public.ingest_token");
        var first = await _fixture.ScalarAsync<DateTime>(_database!, "select last_used_at from public.ingest_token");
        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<DateTime>(_database!, "select last_used_at from public.ingest_token")).ShouldBe(first);

        await _fixture.ExecuteAsync(_database!, "update public.ingest_token set last_used_at = now() - interval '2 minutes'");
        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<DateTime>(_database!, "select last_used_at from public.ingest_token")).ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-1));

        // Bookkeeping is not an edit: a concurrent rotate/revoke with If-Match must not see a spurious version bump.
        (await _fixture.ScalarAsync<int>(_database!, "select version from public.ingest_token")).ShouldBe(version);
    }

    [Fact]
    public async Task IngestToken_Revoke_IsImmediate()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateTokenAsync(client, user, org, project);
        var id = created.Body.GetProperty("token").GetProperty("id").GetGuid();
        var secret = created.Text("secret")!;
        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{id}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);

        var after = await ExchangeIngestAsync(client, secret);
        after.Status.ShouldBe(HttpStatusCode.Unauthorized);
        after.ErrorCode.ShouldBe("token_revoked");

        // Only the holder of the secret learns the state: a wrong secret on a revoked token looks like any unknown token.
        (await ExchangeIngestAsync(client, secret[..^43] + new string('A', 43))).ErrorCode.ShouldBe("token_unknown");
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{id}/rotate", Admin(user, org))).ErrorCode.ShouldBe("token_revoked");
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{Guid.CreateVersion7()}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IngestToken_Rotate_BothVerifyDuringTheOverlap_ThenTheOldOneExpires()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateTokenAsync(client, user, org, project);
        var oldId = created.Body.GetProperty("token").GetProperty("id").GetGuid();
        var oldSecret = created.Text("secret")!;

        var rotated = await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{oldId}/rotate", Admin(user, org));
        rotated.Status.ShouldBe(HttpStatusCode.OK);
        var newSecret = rotated.Text("secret")!;
        newSecret.ShouldNotBe(oldSecret);
        var overlapEnds = rotated.Body.GetProperty("overlap_ends_at").GetDateTimeOffset();
        (overlapEnds - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
        (await _fixture.ScalarAsync<Guid>(_database!, $"select rotated_to_id from public.ingest_token where id = '{oldId}'"))
            .ShouldBe(rotated.Body.GetProperty("token").GetProperty("id").GetGuid());

        (await ExchangeIngestAsync(client, oldSecret)).Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeIngestAsync(client, newSecret)).Status.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{oldId}/rotate", Admin(user, org))).Status.ShouldBe(HttpStatusCode.Conflict);

        await _fixture.ExecuteAsync(_database!, $"update public.ingest_token set overlap_ends_at = now() - interval '1 minute' where id = '{oldId}'");
        var expired = await ExchangeIngestAsync(client, oldSecret);
        expired.Status.ShouldBe(HttpStatusCode.Unauthorized);
        expired.ErrorCode.ShouldBe("token_expired");
        (await ExchangeIngestAsync(client, newSecret)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IngestToken_RetiredPepperVersion_FailsLikeAnUnknownToken()
    {
        var database = _dedicated!.Name;
        string secret;
        Guid org;
        Guid project;
        await using (var first = TenancyApiFactory.Ci(_fixture.AppConnectionString(database)))
        {
            using var client = first.CreateClient();
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(client, user, "acme");
            project = await CreateProjectAsync(client, user, org, "checkout");
            secret = (await CreateTokenAsync(client, user, org, project)).Text("secret")!;
            (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        }

        // The pepper moved on to version 3 and the old one (version 2) was dropped: the token no longer verifies.
        var rotatedKeys = new Dictionary<string, string?>
        {
            ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["EVIDGATE:Ci:PepperVersion"] = "3",
        };
        await using (var retired = TenancyApiFactory.Ci(_fixture.AppConnectionString(database), rotatedKeys))
        {
            using var client = retired.CreateClient();
            (await ExchangeIngestAsync(client, secret)).ErrorCode.ShouldBe("token_unknown");
        }

        // While the previous pepper is still configured (the overlap), the same token verifies.
        var overlap = new Dictionary<string, string?>(rotatedKeys)
        {
            ["EVIDGATE:Ci:PreviousPepper"] = Convert.ToBase64String(TenancyApiFactory.Keys.Pepper),
            ["EVIDGATE:Ci:PreviousPepperVersion"] = "2",
        };
        await using (var during = TenancyApiFactory.Ci(_fixture.AppConnectionString(database), overlap))
        {
            using var client = during.CreateClient();
            (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task IngestToken_Create_ValidatesInput_AndNeedsThePermission()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        foreach (var body in new object[]
                 {
                     new { name = "" },
                     new { name = new string('x', 129) },
                     new { name = "ok", scopes = new[] { "membership:write" } },
                     new { name = "ok", scopes = new[] { "run:create", "run:create" } },
                     new { name = "ok", expires_at = DateTimeOffset.UtcNow.AddDays(-1) },
                 })
        {
            (await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), body))
                .Status.ShouldBe(HttpStatusCode.UnprocessableEntity, body.ToString());
        }

        var narrow = await client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), new { name = "narrow", scopes = EventOnly });
        narrow.Status.ShouldBe(HttpStatusCode.Created);
        var exchanged = await ExchangeIngestAsync(client, narrow.Text("secret")!);
        exchanged.Body.GetProperty("permission_set").EnumerateArray().Select(e => e.GetString()).ShouldBe(["event:write"]);

        var bare = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            _database!,
            $"""
            insert into public."user" (id, email) values ('{bare}', 'bare@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{bare}', 'bare@example.test', 'active')
            """);
        (await CreateTokenAsync(client, bare, org, project)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ingest-tokens", Admin(bare, org))).Status.ShouldBe(HttpStatusCode.Forbidden);

        (await CreateTokenAsync(client, user, org, Guid.CreateVersion7())).Status.ShouldBe(HttpStatusCode.NotFound);
    }
}
