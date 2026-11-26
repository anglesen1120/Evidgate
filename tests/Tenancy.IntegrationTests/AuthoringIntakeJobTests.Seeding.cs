using System.Globalization;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Evidence.Contracts;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringIntakeJobTests
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed record World(string Database, Guid Org, Guid Project, Guid Run, Guid Node);

    /// <summary>One tenant with a project, a feature node and a run, as the owner role writes them.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var node = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            database,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                insert into public.organization (id, name, slug) values ('{org:D}', 'Intake', '{Unique.Slug("intake")}');
                insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'P', '{Unique.Slug("p")}');
                insert into public.feature_node (id, org_id, project_id, kind, title, position)
                values ('{node:D}', '{org:D}', '{project:D}', 'module', 'Checkout', 0);
                """));

        var run = await RunRowSeeder.SeedRunAsync(fixture, database, org);
        await RunRowSeeder.PlanExecutionAsync(fixture, database, org, run, ExecutionKey);

        return new World(database, org, project, run, node);
    }

    /// <summary>A received document intake whose artifact is ready, under the default caps.</summary>
    private Task<Guid> SeedDocumentAsync(World world, byte[] body) =>
        SeedIntakeAsync(world, body.LongLength, IntakeCapsJson.Serialize(IntakeCaps.Default), "docx", DocxType, DocxType);

    /// <summary>One refusal scenario of the theory: the row and the policy are shaped so the job must refuse.</summary>
    private async Task<Guid> SeedScenarioAsync(World world, byte[] body, string scenario)
    {
        var caps = IntakeCaps.Default;
        var declared = body.LongLength;
        var artifactMedia = DocxType;

        switch (scenario)
        {
            case "size":
                caps = caps with { MaxDocumentBytes = declared - 1 };
                break;
            case "media_mismatch":
                artifactMedia = "application/pdf";
                break;
            case "policy_off":
                await fixture.ExecuteAsync(
                    world.Database,
                    "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
                    + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.Project:D}', '{ArtifactKinds.RequirementDocument}', "
                    + $"1048576, array['{DocxType}'], 'normal', false)");
                break;
            default:
                break;
        }

        var json = scenario == "unreadable" ? "{}" : IntakeCapsJson.Serialize(caps);

        return await SeedIntakeAsync(world, declared, json, "docx", DocxType, artifactMedia);
    }

    private async Task<Guid> SeedIntakeAsync(
        World world,
        long declared,
        string capsJson,
        string kind,
        string intakeMedia,
        string artifactMedia)
    {
        var artifact = Guid.CreateVersion7();
        var intake = Guid.CreateVersion7();
        var key = $"org/{world.Org:D}/project/{world.Project:D}/run/{world.Run:D}/attempt/{ExecutionKey}/1/{artifact:D}";
        var checksum = "sha256:" + new string('a', 64);

        await fixture.ExecuteAsync(
            world.Database,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state, bytes, failure_code)
                values ('{artifact:D}', '{world.Org:D}', '{world.Project:D}', '{world.Run:D}', '{ExecutionKey}', 1,
                        '{ArtifactKinds.RequirementDocument}', '{key}', '{artifactMedia}', '{checksum}', 'normal', 'pending', 'ready', {declared}, null);
                insert into public.authoring_intake (id, org_id, project_id, feature_node_id, kind, artifact_id, media_type, declared_bytes, caps)
                values ('{intake:D}', '{world.Org:D}', '{world.Project:D}', '{world.Node:D}', '{kind}', '{artifact:D}', '{intakeMedia}', {declared}, '{capsJson}'::jsonb);
                """));

        return intake;
    }

    private Task<string> StateAsync(World world, Guid intake) => ColumnAsync<string>(world, intake, "state");

    private Task<T> ColumnAsync<T>(World world, Guid intake, string column) =>
        fixture.ScalarAsync<T>(world.Database, $"select {column} from public.authoring_intake where id = '{intake:D}'");

    private Task<long> SlotCountAsync(World world, Guid intake) =>
        fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.authoring_intake_slot where intake_id = '{intake:D}'");

    private Task<long> SlotCountAsync(World world) =>
        fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.authoring_intake_slot where org_id = '{world.Org:D}'");
}
