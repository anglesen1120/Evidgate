// The harness of EvidenceRawReportEnqueueTests: the worlds, the seeded run whose manifest declares producer hints,
// the minted artifacts and the outbox readers the suite asserts on. Kept apart from the facts so neither file grows
// past the size bound; the type is partial, so the fixtures, constants and helpers are one class.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportEnqueueTests
{
    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        string Database,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(bool outOfProcess)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var configuration = _store.Configuration();
        configuration[OutOfProcessKey] = outOfProcess ? "true" : "false";

        // The out-of-process switch is this test's own configuration, so the host stays private instead of pooled.
        var host = _hosts.WithPrincipal(
            database,
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-report-enqueue"),
            configuration);

        return new World(api, host, host.CreateClient(), database, org, project);
    }

    /// <summary>
    /// One committed run whose manifest declares <paramref name="hints"/> on consecutive shards, plus one planned
    /// execution and one producer slot, so the mint has a planned pair and a bound slot. The parents are the seeded
    /// catalogue rows; the manifest is this test's own, because the seeded one carries an empty shard plan.
    /// </summary>
    /// <param name="world">The tenant the run belongs to.</param>
    /// <param name="hints">The producer hints, one per shard; an empty array seeds a shard that declares none.</param>
    private Task<Guid> SeedRunAsync(World world, params string?[] hints) =>
        SeedRunForAsync(world, world.Org, world.Project, hints);

    /// <summary>The same seed for an explicit tenant, so a cross-tenant case can give its own run a declared kind.</summary>
    /// <param name="world">The world whose database and catalogue the rows use.</param>
    /// <param name="org">The owning org.</param>
    /// <param name="project">Its project.</param>
    /// <param name="hints">The producer hints, one per shard.</param>
    private async Task<Guid> SeedRunForAsync(World world, Guid org, Guid project, params string?[] hints)
    {
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

        var runId = Guid.CreateVersion7();
        var manifestId = Guid.CreateVersion7();
        var manifestHash = "sha256:" + manifestId.ToString("N") + manifestId.ToString("N");
        var shardPlan = ShardPlan(hints).Replace("'", "''", StringComparison.Ordinal);

        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            select '{{manifestId}}', p.org_id, p.id, '{{manifestHash}}', 1,
                   (select sr.id
                      from public.suite_revision sr
                      join public.suite s on s.org_id = sr.org_id and s.id = sr.suite_id
                     where sr.org_id = '{{org}}' and s.project_id = p.id
                     order by sr.id limit 1),
                   (select er.id
                      from public.environment_revision er
                     where er.org_id = '{{org}}' and er.project_id = p.id
                     order by er.id limit 1),
                   '{{shardPlan}}'::jsonb, '{}'::jsonb, '{}'::jsonb, 0, '{}'::jsonb, 'probe'
              from public.project p where p.id = '{{project}}'
            """);

        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes)
            select '{{runId}}', m.org_id, m.project_id, m.id, m.suite_revision_id, m.environment_revision_id, now() + interval '2 hours', 1440
              from public.manifest m where m.id = '{{manifestId}}'
            """);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", org));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", org), ("run_id", runId), ("execution_key", ExecutionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, null)",
            ("id", Guid.CreateVersion7()), ("org_id", org), ("run_id", runId), ("producer_id", ProducerId))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }

    /// <summary>
    /// One artifact row of another tenant, written as the migration role: the cross-tenant case needs a row this
    /// tenant must never resolve, and no upload is involved because the completion is refused before any of it.
    /// </summary>
    /// <param name="world">The world whose database is used.</param>
    /// <param name="org">The foreign org.</param>
    /// <param name="project">Its project.</param>
    /// <param name="runId">Its run, so the composite tenant keys hold.</param>
    private async Task<Guid> InsertForeignArtifactAsync(World world, Guid org, Guid project, Guid runId)
    {
        var artifactId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
            + $"values ('{artifactId:D}', '{org:D}', '{project:D}', '{runId:D}', '{ExecutionKey}', 1, 'raw_report', 'pending/{org:D}/{artifactId:D}', '{XmlType}', '{Checksum(ReportBody)}', 'normal', 'pending', 'pending_upload')");

        return artifactId;
    }

    /// <summary>The manifest's shard plan: one shard per hint, each declaring its hint when it has one.</summary>
    /// <param name="hints">The hints, in shard order; empty seeds a single shard with no <c>producer_hint</c>.</param>
    private static string ShardPlan(string?[] hints)
    {
        var shards = new JsonArray();
        var count = Math.Max(hints.Length, 1);

        for (var index = 0; index < count; index++)
        {
            var shard = new JsonObject
            {
                ["shard_id"] = $"shard-{index + 1}",
                ["from_index"] = index,
                ["to_index"] = index + 1,
            };

            if (hints.Length > 0 && hints[index] is { } hint)
            {
                shard["producer_hint"] = hint;
            }

            shards.Add(shard);
        }

        return new JsonObject { ["shards"] = shards }.ToJsonString();
    }

    private static byte[] PadToCap(string prefix)
    {
        var body = new byte[Cap];
        var head = Encoding.UTF8.GetBytes(prefix);
        head.CopyTo(body, 0);

        for (var i = head.Length; i < body.Length; i++)
        {
            body[i] = (byte)' ';
        }

        return body;
    }

    private static string Checksum(byte[] body) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>
    /// The kind policy row the mint resolves, seeded once per kind so two artifacts of one kind share it, then the
    /// presigned <c>PUT</c> of the body.
    /// </summary>
    /// <param name="world">The tenant.</param>
    /// <param name="runId">The run the artifact belongs to.</param>
    /// <param name="kind">The artifact kind.</param>
    /// <param name="mediaType">Its declared media type.</param>
    /// <param name="body">The bytes, exactly the declared length.</param>
    private async Task<Guid> MintAndUploadAsync(World world, Guid runId, string kind, string mediaType, byte[] body)
    {
        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled)
            values ('{{Guid.CreateVersion7()}}', '{{world.Org}}', '{{world.Project}}', '{{kind}}', {{Cap}}, array['{{mediaType}}']::text[], 'normal', true)
            on conflict (org_id, project_id, kind) do nothing
            """);

        var minted = await world.Client.SendAsync(
            HttpMethod.Post,
            "/v1/artifacts/upload-url",
            _ => { },
            new
            {
                run_id = runId,
                producer_id = ProducerId,
                execution_key = ExecutionKey,
                attempt_no = 1,
                kind,
                media_type = mediaType,
                bytes = body.LongLength,
                sha256 = Checksum(body),
            },
            NewKey());

        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);

        var artifactId = minted.Id("artifact_id");
        using var put = await PutAsync(minted, body);
        put.IsSuccessStatusCode.ShouldBeTrue($"the signed PUT must be accepted; status={(int)put.StatusCode}");

        return artifactId;
    }

    /// <summary>Sends the bytes to the presigned URL with exactly the headers the mint answered.</summary>
    private static async Task<HttpResponseMessage> PutAsync(ApiAnswer minted, byte[] body)
    {
        using var http = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Put, minted.Text("upload_url"))
        {
            Content = new ByteArrayContent(body),
        };

        foreach (var header in minted.Body.GetProperty("required_headers").EnumerateObject())
        {
            if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value.GetString()!;
            if (!message.Headers.TryAddWithoutValidation(header.Name, value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        return await http.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static async Task CompleteSucceedsAsync(World world, Guid artifactId, string? idempotencyKey = null)
    {
        var answer = await CompleteAsync(world, artifactId, idempotencyKey);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
    }

    private static Task<ApiAnswer> CompleteAsync(World world, Guid artifactId, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, idempotencyKey ?? NewKey());

    /// <summary>
    /// Lowers a kind policy's byte cap after an artifact of that kind was admitted, so a later reader meets an object
    /// its kind no longer admits: the completion verifies against one cap and the parse reads under another, and only
    /// this write moves the bound between them.
    /// </summary>
    /// <param name="world">The tenant whose policy row is lowered.</param>
    /// <param name="kind">The kind the row belongs to.</param>
    /// <param name="maxBytes">The cap to leave behind, below the object that was already stored.</param>
    private async Task<int> LowerKindCapAsync(World world, string kind, long maxBytes) =>
        await _fixture.ScalarAsync<int>(
            world.Database,
            $"with lowered as (update public.artifact_kind_policy set max_bytes = {maxBytes} "
            + $"where org_id = '{world.Org:D}' and project_id = '{world.Project:D}' and kind = '{kind}' returning 1) "
            + "select count(*)::int from lowered").ConfigureAwait(false);

    /// <summary>
    /// Listens for the enqueue counter and collects its <c>issue</c> tag: the closed outcome vocabulary is what the
    /// suites assert on, and the hint itself must never appear.
    /// </summary>
    private static MeterListener CaptureIssues(out ConcurrentBag<string> issues)
    {
        var bag = new ConcurrentBag<string>();
        issues = bag;

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.RawReportEnqueuedMetricName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == EvidgateTelemetry.RawReportProducerIssueTag && tag.Value is string issue)
                {
                    bag.Add(issue);
                }
            }
        });
        listener.Start();

        return listener;
    }

    private Task<long> OutboxCountAsync(World world, string taskType, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where task_type = '{taskType}' and payload->>'artifact_id' = '{artifactId:D}'");

    /// <summary>The mapping rows one run has, whatever artifact wrote them.</summary>
    private Task<long> OutboxCountForRunAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where task_type = '{ReportParseTask}' and payload->>'run_id' = '{runId:D}'");

    /// <summary>The mapping rows one artifact has, read as the superuser so no tenant filter applies.</summary>
    private Task<long> OutboxCountAnywhereAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where task_type = '{ReportParseTask}' and payload->>'artifact_id' = '{artifactId:D}'");

    private Task<long> DistinctArtifactsScheduledAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            "select count(distinct payload->>'artifact_id') from public.outbox "
            + $"where task_type = '{ReportParseTask}' and payload->>'run_id' = '{runId:D}'");

    private Task<string?> PayloadFieldAsync(World world, Guid artifactId, string field) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select payload->>'{field}' from public.outbox where task_type = '{ReportParseTask}' and payload->>'artifact_id' = '{artifactId:D}'");

    /// <summary>
    /// The row's work payload without the correlation carrier the outbox writer adds and without the issue member, so
    /// the shape a resolved kind carries is asserted exactly; the issue member has its own assertions.
    /// </summary>
    private Task<string?> PayloadWithoutCarrierAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            "select (payload - '_evidgate_correlation' - 'producer_issue')::text from public.outbox "
            + $"where task_type = '{ReportParseTask}' and payload->>'artifact_id' = '{artifactId:D}'");

    private Task<string?> IdempotencyKeyAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select idempotency_key from public.outbox where task_type = '{ReportParseTask}' and payload->>'artifact_id' = '{artifactId:D}'");

    /// <summary>
    /// Spends a complete run's late window: a zero-minute window read by a later transaction is a run that admits no new
    /// report, which is the terminal state the admission case names. The clock comparison is the proof, never a sleep.
    /// </summary>
    private async Task SpendLateWindowAsync(World world, Guid runId)
    {
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set late_window_minutes = 0 where id = '{runId:D}'");
        (await _fixture.ScalarAsync<bool>(
            world.Database, $"select now() > completed_at from public.run where id = '{runId:D}'"))
            .ShouldBeTrue("the refusal needs the window spent, which the later transaction's clock proves");
    }
}
