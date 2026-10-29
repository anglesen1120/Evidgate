using System.Net;
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
        // Every shape the guard must walk, with the value that must never appear in the message.
        var rejected = new (string Path, object Payload, string Leak)[]
        {
            ("payload.email", new Dictionary<string, object?> { ["email"] = "person@example.test" }, "person@example.test"),
            ("payload.Name", new Dictionary<string, object?> { ["Name"] = "Person Name" }, "Person Name"),
            ("payload.nested.email", new Dictionary<string, object?>
            {
                ["nested"] = new Dictionary<string, object?> { ["email"] = "person@example.test" },
            }, "person@example.test"),
            ("payload.items[0].user_agent", new Dictionary<string, object?>
            {
                ["items"] = new object?[] { new Dictionary<string, object?> { ["user_agent"] = "Mozilla/5.0" } },
            }, "Mozilla/5.0"),
            ("payload[0].ip", new Dictionary<string, object?>[] { new() { ["ip"] = "203.0.113.7" } }, "203.0.113.7"),
            ("payload.free-text", new Dictionary<string, object?> { ["free-text"] = "asked us to stop" }, "asked us to stop"),
            ("payload.USER_AGENT", new Dictionary<string, object?> { ["USER_AGENT"] = "Mozilla/5.0" }, "Mozilla/5.0"),
            ("payload.UserAgent", new Dictionary<string, object?> { ["UserAgent"] = "Mozilla/5.0" }, "Mozilla/5.0"),
            ("payload.reasonText", new { reasonText = "asked for a correction" }, "asked for a correction"),
            ("payload.comment", JsonNode.Parse("""{"comment":"please delete my data"}""")!, "please delete my data"),
            ("payload[0].description", JsonNode.Parse("""[{"description":"please remove the record"}]""")!, "please remove the record"),
        };

        foreach (var (path, payload, leak) in rejected)
        {
            var exception = Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(payload));
            exception.Message.ShouldContain(path);
            exception.Message.ShouldNotContain(leak, Case.Sensitive, "the message names the key path only, never a value");
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
