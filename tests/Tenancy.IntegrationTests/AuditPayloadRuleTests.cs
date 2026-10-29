using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 11.9.2-T2.2 / AC10: the audit payload schema rejects a personal-data or free-text key — top level,
/// nested object, array element, any casing or separator — before any hash, advisory lock or insert, and
/// an allowlisted payload still writes its row unchanged.
/// <para>
/// The deny list is <see cref="AuditMetadata.ForbiddenKeys"/>; the guard runs in
/// <see cref="AuditMetadata.Validate"/> (which <see cref="AuditTrail.Describe"/> calls, so nothing is
/// queued) and once more in <see cref="AuditTrail.FlushAsync"/> before the lock.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuditPayloadRuleTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuditPayloadRuleTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AuditPayload_ForbiddenKeys_Rejected()
    {
        // Every shape the guard must walk: the key it must name, that key's depth, the value that must
        // never appear in the message, and an ancestor key name that must not appear either (a nested
        // key may itself be personal data, so the message names only the offending key and its depth).
        using var document = JsonDocument.Parse("""{"wrapper":{"email":"person@example.test"}}""");

        var rejected = new (string Key, int Depth, string Leak, string? Ancestor, object Payload)[]
        {
            ("email", 1, "person@example.test", null, new Dictionary<string, object?> { ["email"] = "person@example.test" }),
            ("Name", 1, "Person Name", null, new Dictionary<string, object?> { ["Name"] = "Person Name" }),
            ("email", 2, "person@example.test", "wrapper", new Dictionary<string, object?>
            {
                ["wrapper"] = new Dictionary<string, object?> { ["email"] = "person@example.test" },
            }),
            ("user_agent", 3, "Mozilla/5.0", "items", new Dictionary<string, object?>
            {
                ["items"] = new object?[] { new Dictionary<string, object?> { ["user_agent"] = "Mozilla/5.0" } },
            }),
            ("ip", 2, "203.0.113.7", null, new Dictionary<string, object?>[] { new() { ["ip"] = "203.0.113.7" } }),
            ("free-text", 1, "asked us to stop", null, new Dictionary<string, object?> { ["free-text"] = "asked us to stop" }),
            ("USER_AGENT", 1, "Mozilla/5.0", null, new Dictionary<string, object?> { ["USER_AGENT"] = "Mozilla/5.0" }),
            ("UserAgent", 1, "Mozilla/5.0", null, new Dictionary<string, object?> { ["UserAgent"] = "Mozilla/5.0" }),
            ("reasonText", 1, "asked for a correction", null, new { reasonText = "asked for a correction" }),
            ("comment", 1, "please delete my data", null, JsonNode.Parse("""{"comment":"please delete my data"}""")!),
            ("description", 2, "please remove the record", null, JsonNode.Parse("""[{"description":"please remove the record"}]""")!),
            // Unicode: the fullwidth spelling normalises to EMAIL; the Cyrillic homoglyph keeps a non-ASCII
            // letter after form KC and is refused by the fail-closed rule instead of matching the deny list.
            ("\uFF45\uFF4D\uFF41\uFF49\uFF4C", 1, "person@example.test", null, new Dictionary<string, object?>
            {
                ["\uFF45\uFF4D\uFF41\uFF49\uFF4C"] = "person@example.test",
            }),
            ("\u0435mail", 1, "person@example.test", null, new Dictionary<string, object?>
            {
                ["\u0435mail"] = "person@example.test",
            }),
            // A parsed JsonElement is walked like any other object.
            ("email", 2, "person@example.test", "wrapper", document.RootElement),
        };

        foreach (var (key, depth, leak, ancestor, payload) in rejected)
        {
            var exception = Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(payload));
            exception.Message.ShouldContain(key);
            exception.Message.ShouldContain($"at depth {depth}");
            exception.Message.ShouldNotContain(leak, Case.Sensitive, "the message names the key and its depth only, never a value");
            if (ancestor is not null)
            {
                exception.Message.ShouldNotContain(ancestor, Case.Sensitive, "an ancestor key name may itself be personal data");
            }
        }

        // The allowlist still passes, including keys that merely contain a term of the deny list.
        AuditMetadata.Validate(new Dictionary<string, string>
        {
            ["slug"] = "acme",
            ["email_domain"] = "example.test",
            ["reason"] = "retention",
        }).ShouldNotBeNull();
        AuditMetadata.EnsureNoForbiddenKeys(new Dictionary<string, string> { ["email_domain"] = "example.test", ["slug"] = "acme" });

        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");

        // The org creation wrote seq 1; that is the chain head the rejected payload must leave alone.
        var before = await CheckpointAsync(database, org);
        before.Count.ShouldBe(1);

        await using (var attempt = await WriterScope.BeginAsync(api, org))
        {
            var exception = Should.Throw<ArgumentException>(() => attempt.Audit.Describe(
                new AuditObject("user", user.ToString("D"), Metadata: new Dictionary<string, string>
                {
                    ["USER-AGENT"] = "Mozilla/5.0",
                })));

            exception.Message.ShouldContain("USER-AGENT");
            exception.Message.ShouldNotContain("Mozilla", Case.Sensitive, "never echo the value of a personal field");

            // Nothing was queued, so the flush the pipeline runs writes no row and takes no lock.
            (await attempt.FlushAuditAsync()).ShouldBe(0);
        }

        var after = await CheckpointAsync(database, org);
        after.ShouldBe(before);
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.audit_log where org_id = '{org}'")).ShouldBe(1);

        // An allowlisted payload still passes and writes exactly one row, with its metadata intact.
        await using (var allowed = await WriterScope.BeginAsync(api, org))
        {
            allowed.Audit.Describe(new AuditObject("project", Guid.CreateVersion7().ToString("D"), Metadata: new Dictionary<string, string>
            {
                ["slug"] = "acme",
                ["email_domain"] = "example.test",
            }));
            allowed.Audit.Record("project.create", new AuditActor(AuditActorTypes.User, user.ToString("D")));
            (await allowed.FlushAuditAsync()).ShouldBe(1);
            await allowed.CommitAsync();
        }

        var written = await CheckpointAsync(database, org);
        written.Count.ShouldBe(2);
        written[^1].Seq.ShouldBe(2);
        (await _fixture.ScalarAsync<string>(
            database,
            $"select metadata::text from public.audit_log where org_id = '{org}' and seq = 2")).ShouldBe(
            """{"slug": "acme", "email_domain": "example.test"}""");
        (await VerifyAsync(api, org)).ShouldBeNull();
    }

    [Fact]
    public async Task AuditPayload_MutatedAfterDescribe_RefusedAtFlush()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");

        // The org creation wrote seq 1; that is the chain head this unit must leave alone.
        var before = await CheckpointAsync(database, org);
        before.Count.ShouldBe(1);

        await using (var attempt = await WriterScope.BeginAsync(api, org))
        {
            // Describe validates the metadata and Record keeps the same dictionary instance, so mutating that
            // instance afterwards is the only route past the first gate — and the flush gate must catch it.
            var metadata = new Dictionary<string, string>
            {
                ["slug"] = "acme",
                ["email_domain"] = "example.test",
            };
            attempt.Audit.Describe(new AuditObject(
                "project",
                Guid.CreateVersion7().ToString("D"),
                Metadata: metadata));
            attempt.Audit.Record("project.create", new AuditActor(AuditActorTypes.User, user.ToString("D")));

            metadata["email"] = "person@example.test";

            var exception = await Should.ThrowAsync<ArgumentException>(() => attempt.FlushAuditAsync());

            exception.Message.ShouldContain("email");
            exception.Message.ShouldContain("at depth 1");
            exception.Message.ShouldNotContain("person@example.test", Case.Sensitive, "never echo the value of a personal field");
        }

        // The guard runs before lock_timeout, the advisory lock, the hash and the insert: no row, no delta.
        var after = await CheckpointAsync(database, org);
        after.ShouldBe(before);
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.audit_log where org_id = '{org}'")).ShouldBe(1);
        (await VerifyAsync(api, org)).ShouldBeNull();
    }

    private async Task<IReadOnlyList<(long Seq, string EntryHash, string PrevHash)>> CheckpointAsync(string database, Guid org)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = new NpgsqlCommand(
            "select seq, encode(entry_hash, 'hex'), encode(prev_hash, 'hex') from public.audit_log where org_id = @org order by seq",
            connection);
        command.Parameters.AddWithValue("org", org);

        var rows = new List<(long Seq, string EntryHash, string PrevHash)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<AuditChainFinding?> VerifyAsync(TenancyApiFactory api, Guid org)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var scopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = scope.ServiceProvider.GetRequiredService<ITenantDb>();
        await using var tenant = await scopes.BeginAsync(org, null, true, TestContext.Current.CancellationToken);

        return await new AuditChainVerifier(db).VerifyAsync(org, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient client, Guid user, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    /// <summary>
    /// One tenant transaction, the audit unit and the Dapper path, opened the way the C2 pipeline opens
    /// them (the <see cref="RunTransitionScope"/> shape of 05.3.3-T2): the unit and the tenant scope are
    /// published into the ambient <c>AsyncLocal</c> slots in the caller's execution context, so the
    /// factory's task is continued rather than awaited inside an <c>async</c> helper
    /// (tests/README.md, "AsyncLocal rule").
    /// </summary>
    private sealed class WriterScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _services;
        private readonly ITenantScope _scope;
        private readonly IDisposable _auditUnit;
        private readonly ITenantDb _db;

        private WriterScope(AsyncServiceScope services, ITenantScope scope, IDisposable auditUnit)
        {
            _services = services;
            _scope = scope;
            _auditUnit = auditUnit;
            _db = services.ServiceProvider.GetRequiredService<ITenantDb>();
            Audit = services.ServiceProvider.GetRequiredService<IAuditTrail>();
        }

        internal IAuditTrail Audit { get; }

        internal static Task<WriterScope> BeginAsync(TenancyApiFactory api, Guid org)
        {
            var services = api.Services.CreateAsyncScope();
            var provider = services.ServiceProvider;
            var auditUnit = provider.GetRequiredService<IAuditTrail>().BeginUnit();
            var scopes = provider.GetRequiredService<ITenantScopeFactory>();

            return scopes
                .BeginAsync(org, null, false, TestContext.Current.CancellationToken)
                .ContinueWith(
                    opened => new WriterScope(services, opened.GetAwaiter().GetResult(), auditUnit),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        internal Task<int> FlushAuditAsync() =>
            Audit.FlushAsync(_scope.OrgId, _db, TestContext.Current.CancellationToken);

        internal Task CommitAsync() => _scope.CommitAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            _auditUnit.Dispose();
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
        }
    }
}
