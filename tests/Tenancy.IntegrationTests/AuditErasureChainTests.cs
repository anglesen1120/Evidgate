using System.Net;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 11.9.2-T2.2 / AC9-AC10: erasing a person's personal fields keeps the audit chain valid. The rows the
/// person's actions wrote stay — pseudonymous actor/object id, before/after hashes, no personal data —
/// so the chain verifier still reports <c>ok</c> and every chain anchor (each row's <c>seq</c>,
/// <c>prev_hash</c> and <c>entry_hash</c>, i.e. the input of any checkpoint) is byte-identical.
/// <para>
/// 11.5.2-T2's signed external checkpoints are not delivered yet (ADR-0010 is <c>proposed</c>), so the
/// "checkpoint" this test pins is the chain itself: the tuple a checkpoint would attest. Read
/// "checkpoint" here as the chain anchors — each row's <c>seq</c>, <c>prev_hash</c> and <c>entry_hash</c> —
/// not a signed checkpoint, which does not exist in code yet.
/// </para>
/// <para>
/// The audit chain is not the only personal-data store: <c>public.user_identity</c> (the provider address,
/// <c>email_at_provider</c>) and <c>membership.invited_email</c> hold addresses too. Erasing those is the
/// erasure executor's job (11.9.2-T1.4, on the 11.9.1-T1 verified-deletion path), not this test's: this
/// test erases only the <c>"user"</c> row's personal fields and proves the chain never carried any.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuditErasureChainTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuditErasureChainTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Erasure_ThenChainVerify_Ok_CheckpointsUnchanged()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        // The person's record, as the identity module stores it (email + provider display name only).
        var user = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.\"user\" (id, email, display_name) values ('{user}', 'person@example.test', 'Person Name')");

        var org = await CreateOrgAsync(client, user, "acme");
        await CreateProjectAsync(client, user, org, "checkout");

        var before = await CheckpointAsync(database, org);
        before.Count.ShouldBe(2);
        before.Select(row => row.ActorId).Distinct().ShouldBe([user.ToString("D")]);
        (await VerifyAsync(api, org)).ShouldBeNull();

        // Before erasure the chain already carries no personal data — nothing personal is summed into the
        // entry hash (audit-and-deletion-rule.md §3), which is exactly why erasure has nothing to rewrite
        // (§5). The values are the ones written above and in CreateOrgAsync's resolved claim.
        (await PersonalDataInAuditAsync(database, org, "person@example.test", "acme@example.test", "Person Name"))
            .ShouldBe(0, "no audit row of the org may carry the person's address or display name before erasure");

        // Erasure: the personal fields go, the id stays as the tombstone (11.9.2 AC8/AC9) — no audit row
        // is written or removed by the erasure itself.
        await _fixture.ExecuteAsync(
            database,
            $"update public.\"user\" set email = 'erased-' || id::text, display_name = null, last_login_at = null where id = '{user}'");

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.\"user\" where id = '{user}' and email = 'erased-{user}' and display_name is null and last_login_at is null"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.\"user\" where id = '{user}' and (display_name is not null or email like '%@%')"))
            .ShouldBe(0);

        // And the chain is exactly as clean after the erasure: erasure writes and removes no audit row, so
        // the negative-content count must not have moved either.
        (await PersonalDataInAuditAsync(database, org, "person@example.test", "acme@example.test", "Person Name"))
            .ShouldBe(0, "erasure leaves the chain without the person's address or display name, as it was before");

        (await VerifyAsync(api, org)).ShouldBeNull();

        var after = await CheckpointAsync(database, org);
        after.ShouldBe(before);
        after[^1].EntryHash.ShouldBe(before[^1].EntryHash, "the chain head is the checkpoint input and it did not move");
    }

    /// <summary>Every chain anchor (the input of any checkpoint) as hex, so the comparison is by value.</summary>
    private async Task<IReadOnlyList<(long Seq, string ActorId, string PrevHash, string EntryHash)>> CheckpointAsync(
        string database,
        Guid org)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = new NpgsqlCommand(
            "select seq, actor_id, encode(prev_hash, 'hex'), encode(entry_hash, 'hex') from public.audit_log where org_id = @org order by seq",
            connection);
        command.Parameters.AddWithValue("org", org);

        var rows = new List<(long Seq, string ActorId, string PrevHash, string EntryHash)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Audit rows of <paramref name="org"/> that carry the person's personal data: any of
    /// <paramref name="personalValues"/> (the addresses and the display name the person's record and the
    /// resolved claim are written with) anywhere in <c>metadata</c>, or an email-shaped
    /// <c>actor_id</c>/<c>object_id</c>. The contract is zero, before and after the erasure: the chain keeps
    /// the pseudonymous id only, so there is no personal field for an erasure to rewrite (11.9.2-T2.2,
    /// AC9; audit-and-deletion-rule.md §3, §5).
    /// </summary>
    private async Task<long> PersonalDataInAuditAsync(
        string database,
        Guid org,
        params string[] personalValues)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = new NpgsqlCommand(
            """
            select count(*)
              from public.audit_log
             where org_id = @org
               and (metadata::text ilike any(@patterns)
                    or actor_id ilike '%@%'
                    or object_id ilike '%@%')
            """,
            connection);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("patterns", Array.ConvertAll(personalValues, value => $"%{value}%"));

        var token = TestContext.Current.CancellationToken;
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);

        return await reader.GetFieldValueAsync<long>(0, token);
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

    private static async Task<Guid> CreateProjectAsync(HttpClient client, Guid user, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Resolved(user: user), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }
}
