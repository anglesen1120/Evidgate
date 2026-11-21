using System.Net;
using Evidgate.BuildingBlocks.Observability;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

// Fault injection: the two refusals only a lost write reaches (the AI settings unique-version loss and the apply flip
// that moves fewer rows than it was handed) are driven by test-owned triggers in the world's own database. Each
// trigger counts its firing, so a refusal reached through an earlier branch cannot satisfy the test.
public sealed partial class AuthoringCommitFaultMetricTests
{
    private const string CompetitorHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task Save_VersionKeyLostToAnotherWriter_Answers412WithTheCompetitorsVersionCountsStaleAndStoresNothingOfTheRequest()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        using var capture = new AuthoringMetricCapture();
        try
        {
            await ArmCompetingInsertAsync(world);
            var refused = await AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

            refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed, refused.ErrorMessage);
            AiSettingsCalls.Details(refused).ShouldBe([("version", "1")]);
            capture.Taken.ShouldBe([$"{EvidgateTelemetry.AiSettingsWriteMetricName}|1|outcome=stale"]);
            (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.fault_fired")).ShouldBe(1);

            // The trigger's competitor is the only row for version 1; the request's own content is not stored.
            (await CountAsync(world, "project_ai_settings", "version_no = 1")).ShouldBe(1);
            (await CountAsync(world, "project_ai_settings", $"version_no = 1 and content_hash = '{CompetitorHash}'")).ShouldBe(1);
            (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(1);
            (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(0);
        }
        finally
        {
            await DisarmLostWriteFaultsAsync(world);
        }
    }

    [Fact]
    public async Task Apply_FlipMovesFewerRowsThanSelected_Answers409ChangeNotPendingCountsConflictAndRollsBackTheAppend()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var item = await CreateItemAsync(world);
        var queued = await QueueAsync(world, item, NewKey());
        queued.Status.ShouldBe(HttpStatusCode.Created, queued.ErrorMessage);
        var change = queued.Id("change_id");
        using var capture = new AuthoringMetricCapture();
        try
        {
            await ArmSkippedFlipAsync(world);
            var refused = await ApplyAsync(world, item, change, NewKey());

            refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
            refused.ErrorCode.ShouldBe("invalid_transition");
            AiSettingsCalls.Details(refused).ShouldBe([("/change_ids", "change_not_pending")]);
            capture.Taken.ShouldBe([$"{EvidgateTelemetry.AuthoringChangeApplyMetricName}|1|outcome=conflict"]);
            (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.fault_fired")).ShouldBe(1);

            // Every change is still pending and the version append of the same request rolled back.
            (await CountAsync(world, "authoring_change", "state <> 'pending' or applied_batch_id is not null")).ShouldBe(0);
            (await CountAsync(world, "authoring_change", $"id = '{change}' and state = 'pending'")).ShouldBe(1);
            (await CountAsync(world, "authoring_version", VersionPredicate(item, 2))).ShouldBe(0);
            (await CountAsync(world, "checklist_item", $"id = '{item}' and deleted_at is null and version = 1")).ShouldBe(1);
        }
        finally
        {
            await DisarmLostWriteFaultsAsync(world);
        }
    }

    // A writer this transaction did not serialise with takes the version number first: the trigger inserts a row with
    // the incoming version before the real insert reaches its unique-key arbiter.
    private Task ArmCompetingInsertAsync(AiSettingsCalls.World world) =>
        fixture.ExecuteAsync(world.Database, $"""
            drop function if exists public.ai_settings_lost_write_fn() cascade;
            drop table if exists public.fault_fired;
            create table public.fault_fired (n int not null);
            create function public.ai_settings_lost_write_fn() returns trigger
              language plpgsql security definer set search_path = '' as $fault$
            declare competitor public.project_ai_settings;
            begin
              if pg_trigger_depth() = 1 then
                insert into public.fault_fired values (1);
                competitor := new;
                competitor.id := gen_random_uuid();
                competitor.content_hash := '{CompetitorHash}';
                insert into public.project_ai_settings select competitor.*;
              end if;
              return new;
            end $fault$;
            create trigger ai_settings_lost_write before insert on public.project_ai_settings
              for each row execute function public.ai_settings_lost_write_fn();
            """);

    // The change rows are locked before the flip, so no real writer can shrink its count; the trigger skips only the
    // pending to applied update, the way a vanished row would.
    private Task ArmSkippedFlipAsync(AiSettingsCalls.World world) =>
        fixture.ExecuteAsync(world.Database, """
            drop function if exists public.authoring_flip_skip_fn() cascade;
            drop table if exists public.fault_fired;
            create table public.fault_fired (n int not null);
            create function public.authoring_flip_skip_fn() returns trigger
              language plpgsql security definer set search_path = '' as $fault$
            begin
              if old.state = 'pending' and new.state = 'applied' then
                insert into public.fault_fired values (1);
                return null;
              end if;
              return new;
            end $fault$;
            create trigger zz_authoring_flip_skip before update on public.authoring_change
              for each row execute function public.authoring_flip_skip_fn();
            """);

    private Task DisarmLostWriteFaultsAsync(AiSettingsCalls.World world) =>
        fixture.ExecuteAsync(world.Database, """
            drop function if exists public.ai_settings_lost_write_fn() cascade;
            drop function if exists public.authoring_flip_skip_fn() cascade;
            drop table if exists public.fault_fired;
            """);
}
