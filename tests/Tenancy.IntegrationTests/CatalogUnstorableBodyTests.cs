using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Every raw-body Catalog route through the real host: a member name or string that jsonb cannot store is refused as
/// 422 <c>request_invalid</c> before any member is read, without echoing the body, writing a row or claiming the key.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CatalogUnstorableBodyTests(
    PostgresTenancyFixture fixture,
    TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private sealed record Target(
        HttpMethod Method,
        string Route,
        string ValidBody,
        string Table,
        HttpStatusCode Success,
        bool CreatesRow = true,
        string? IfMatch = null,
        bool DryRun = false,
        bool Recover = true);

    private Task<World> StartAsync() => RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

    private static Task<ApiAnswer> SendAsync(World world, Target target, string body, string key) =>
        world.Client.SendRawAsync(target.Method, target.Route, headers =>
        {
            world.Admin(headers);
            headers.Add("Idempotency-Key", key);
            if (target.IfMatch is not null)
            {
                headers.Add("If-Match", target.IfMatch);
            }
        }, body);

    private static void AssertRefusal(ApiAnswer response, string path = "", string reason = "invalid_value")
    {
        var text = response.Body.GetRawText();
        response.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, text);
        response.ErrorCode.ShouldBe("request_invalid");
        var detail = response.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
        if (path.Length == 0)
        {
            response.ErrorMessage.ShouldBe("the body carries text that cannot be stored");
        }

        text.Contains("SECRETMARK", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        text.Contains("marker-zq7", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        text.Contains("\\ud800", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        text.Contains("\\u0000", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    private async Task AssertRefusedAndRecoverAsync(
        World world,
        Target target,
        string malformed,
        string path = "",
        string reason = "invalid_value",
        bool recover = true)
    {
        var key = NewKey();
        var before = await SnapshotAsync(world);
        var rows = await CountAsync(world, target.Table);

        var refused = await SendAsync(world, target, malformed, key);

        AssertRefusal(refused, path, reason);
        (await RecordCountAsync(world, key)).ShouldBe(0);
        (await SnapshotAsync(world)).ShouldBe(before,
            "a refusal changes no audit, outbox, task, Catalog row or case field");
        if (!target.Recover || !recover)
        {
            return;
        }

        var accepted = await SendAsync(world, target, target.ValidBody, key);
        accepted.Status.ShouldBe(target.Success, accepted.Body.GetRawText());
        (await RecordCountAsync(world, key)).ShouldBe(target.DryRun ? 0 : 1);
        (await CountAsync(world, target.Table)).ShouldBe(rows + (target.CreatesRow ? 1 : 0));
        if (target.DryRun)
        {
            (await SnapshotAsync(world)).ShouldBe(before, "even an accepted preview writes nothing");
        }
    }

    private async Task AssertUnicodeReplayAsync(World world, Target target)
    {
        var key = NewKey();
        var rows = await CountAsync(world, target.Table);
        var first = await SendAsync(world, target, target.ValidBody, key);
        first.Status.ShouldBe(target.Success, first.Body.GetRawText());
        (await CountAsync(world, target.Table)).ShouldBe(rows + 1);
        (await RecordCountAsync(world, key)).ShouldBe(1);
        var hash = await fixture.ScalarAsync<string>(world.Database,
            $"select request_hash from public.idempotency_record where org_id = '{world.Org}' and key = '{key}'");
        hash.ShouldNotBeNullOrWhiteSpace();
        var beforeReplay = await SnapshotAsync(world);

        var replay = await SendAsync(world, target, target.ValidBody, key);

        replay.Status.ShouldBe(first.Status, replay.Body.GetRawText());
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await RecordCountAsync(world, key)).ShouldBe(1);
        (await fixture.ScalarAsync<string>(world.Database,
            $"select request_hash from public.idempotency_record where org_id = '{world.Org}' and key = '{key}'")).ShouldBe(hash);
        (await SnapshotAsync(world)).ShouldBe(beforeReplay);
    }

    private Task<long> CountAsync(World world, string table) =>
        fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org}'");

    private Task<long> RecordCountAsync(World world, string key) =>
        fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org}' and key = '{key}'");

    private Task<string> SnapshotAsync(World world) => fixture.ScalarAsync<string>(world.Database, $"""
        select jsonb_build_array(
            (select count(*) from public.audit_log where org_id = '{world.Org}'),
            (select count(*) from public.outbox where org_id = '{world.Org}'),
            (select count(*) from public.task where org_id = '{world.Org}'),
            (select count(*) from public.suite_revision where org_id = '{world.Org}'),
            (select count(*) from public.fixture where org_id = '{world.Org}'),
            (select count(*) from public.fixture_revision where org_id = '{world.Org}'),
            (select count(*) from public.git_repo_binding where org_id = '{world.Org}'),
            (select count(*) from public.test_revision where org_id = '{world.Org}'),
            (select coalesce(jsonb_agg(to_jsonb(c) order by c.id), '[]'::jsonb)
                from public.test_case c where c.org_id = '{world.Org}')
        )::text
        """);
}
