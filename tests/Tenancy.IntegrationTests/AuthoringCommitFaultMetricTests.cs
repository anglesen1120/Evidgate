using System.Net;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The three authoring success instruments are recorded after commit: a request whose commit fails records no success
/// sample and persists nothing, and the same request retried with the fault off records exactly one.
/// </summary>
/// <remarks>
/// The fault is a test-owned deferred constraint trigger that raises while its flag row exists, so the handler's writes
/// complete and the failure lands at COMMIT. The meter is process-global, hence the exclusive collection.
/// </remarks>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class AuthoringCommitFaultMetricTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Save_CommitFails_RecordsCreatedOnlyAfterRetryCommits()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var body = AiSettingsCalls.Body(AiSettingsCalls.Document());
        var key = NewKey();
        using var capture = new AuthoringMetricCapture();
        try
        {
            await ArmCommitFaultAsync(world, "project_ai_settings");
            var failed = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"", key);

            failed.Status.ShouldBe(HttpStatusCode.InternalServerError, failed.ErrorMessage);
            failed.ErrorCode.ShouldBe("internal_error");
            capture.Taken.ShouldBeEmpty();
            (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(0);

            // The begin row of the claimed key rolls back with the same transaction, so the key stays retryable.
            await DisarmCommitFaultAsync(world);
            var retried = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"", key);

            retried.Status.ShouldBe(HttpStatusCode.Created, retried.ErrorMessage);
            (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(1);
            capture.Taken.ShouldBe([$"{EvidgateTelemetry.AiSettingsWriteMetricName}|1|outcome=created"]);
        }
        finally
        {
            await DisarmCommitFaultAsync(world);
        }
    }

    [Fact]
    public async Task Queue_CommitFails_RecordsQueuedOnlyAfterRetryCommits()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var item = await CreateItemAsync(world);
        var key = NewKey();
        using var capture = new AuthoringMetricCapture();
        try
        {
            await ArmCommitFaultAsync(world, "authoring_change");
            var failed = await QueueAsync(world, item, key);

            failed.Status.ShouldBe(HttpStatusCode.InternalServerError, failed.ErrorMessage);
            failed.ErrorCode.ShouldBe("internal_error");
            capture.Taken.ShouldBeEmpty();
            (await CountAsync(world, "authoring_change")).ShouldBe(0);

            await DisarmCommitFaultAsync(world);
            var retried = await QueueAsync(world, item, key);

            retried.Status.ShouldBe(HttpStatusCode.Created, retried.ErrorMessage);
            (await CountAsync(world, "authoring_change", "state = 'pending'")).ShouldBe(1);
            capture.Taken.ShouldBe([$"{EvidgateTelemetry.AuthoringChangeQueuedMetricName}|1|change_type=delete"]);
        }
        finally
        {
            await DisarmCommitFaultAsync(world);
        }
    }

    [Fact]
    public async Task Apply_CommitFails_RecordsAppliedAndBatchSizeOnlyAfterRetryCommits()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var item = await CreateItemAsync(world);
        var queued = await QueueAsync(world, item, NewKey());
        queued.Status.ShouldBe(HttpStatusCode.Created, queued.ErrorMessage);
        var change = queued.Id("change_id");
        var key = NewKey();
        using var capture = new AuthoringMetricCapture();
        try
        {
            await ArmCommitFaultAsync(world, "authoring_change");
            var failed = await ApplyAsync(world, item, change, key);

            failed.Status.ShouldBe(HttpStatusCode.InternalServerError, failed.ErrorMessage);
            failed.ErrorCode.ShouldBe("internal_error");
            capture.Taken.ShouldBeEmpty();
            (await CountAsync(world, "authoring_change", "state = 'applied'")).ShouldBe(0);
            (await CountAsync(world, "authoring_change",
                $"id = '{change}' and state = 'pending' and applied_at is null and applied_batch_id is null")).ShouldBe(1);
            (await CountAsync(world, "authoring_version", VersionPredicate(item, 2))).ShouldBe(0);
            (await CountAsync(world, "checklist_item", $"id = '{item}' and deleted_at is null and version = 1")).ShouldBe(1);

            await DisarmCommitFaultAsync(world);
            var retried = await ApplyAsync(world, item, change, key);

            retried.Status.ShouldBe(HttpStatusCode.OK, retried.ErrorMessage);
            (await CountAsync(world, "authoring_change", $"id = '{change}' and state = 'applied'")).ShouldBe(1);
            (await CountAsync(world, "authoring_version", VersionPredicate(item, 2))).ShouldBe(1);
            (await CountAsync(world, "checklist_item", $"id = '{item}' and deleted_at is not null and version = 2")).ShouldBe(1);
            capture.Taken.ShouldBe(
            [
                $"{EvidgateTelemetry.AuthoringChangeApplyMetricName}|1|outcome=applied",
                $"{EvidgateTelemetry.AuthoringChangeApplyBatchSizeMetricName}|1|",
            ]);
        }
        finally
        {
            await DisarmCommitFaultAsync(world);
        }
    }

    private static string VersionPredicate(Guid item, int versionNo) =>
        $"entity_kind = 'checklist_item' and entity_id = '{item}' and version_no = {versionNo}";

    private async Task<Guid> CreateItemAsync(AiSettingsCalls.World world)
    {
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{module}', '{world.Org}', '{world.Project}', null, 'module', 'Shop', 0),
                   ('{feature}', '{world.Org}', '{world.Project}', '{module}', 'feature', 'Checkout', 0),
                   ('{category}', '{world.Org}', '{world.Project}', '{feature}', 'category', 'Payment', 0);
            """);
        var created = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project:D}/authoring/checklist-items", world.As,
            new { node_id = category, title = "Item", priority = "high" }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("item_id");
    }

    private static Task<ApiAnswer> QueueAsync(AiSettingsCalls.World world, Guid item, string key) =>
        world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project:D}/authoring/changes", world.As,
            new { target_kind = "checklist_item", target_id = item, change_type = "delete", payload = new { } }, key);

    private static Task<ApiAnswer> ApplyAsync(AiSettingsCalls.World world, Guid item, Guid change, string key) =>
        world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project:D}/authoring/changes:apply", world.As,
            new { change_ids = new[] { change } }, key, $"\"{AuthoringHashes.OrderHash(new[] { (item, 1) })}\"");

    private Task<long> CountAsync(AiSettingsCalls.World world, string table, string predicate = "true") =>
        fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org}' and project_id = '{world.Project}' and ({predicate})");

    private Task ArmCommitFaultAsync(AiSettingsCalls.World world, string table) =>
        fixture.ExecuteAsync(world.Database, $"""
            drop function if exists public.authoring_commit_fault_fn() cascade;
            drop table if exists public.authoring_commit_fault;
            create table public.authoring_commit_fault (org_id uuid not null, project_id uuid not null primary key);
            insert into public.authoring_commit_fault values ('{world.Org}', '{world.Project}');
            create function public.authoring_commit_fault_fn() returns trigger
              language plpgsql security definer set search_path = '' as $fault$
            begin
              if exists (select 1 from public.authoring_commit_fault f
                         where f.org_id = new.org_id and f.project_id = new.project_id) then
                raise exception 'authoring commit fault' using errcode = 'P0001';
              end if;
              return null;
            end $fault$;
            create constraint trigger authoring_commit_fault_write
              after insert or update on public.{table}
              deferrable initially deferred
              for each row execute function public.authoring_commit_fault_fn();
            """);

    private Task DisarmCommitFaultAsync(AiSettingsCalls.World world) =>
        fixture.ExecuteAsync(world.Database, """
            drop function if exists public.authoring_commit_fault_fn() cascade;
            drop table if exists public.authoring_commit_fault;
            """);
}
