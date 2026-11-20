using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Testing;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The arrange half of the budget run: the pinned report, and the plan whose identities are the report's own — one
/// planned execution per testcase in the shape pytest's dotted <c>classname</c> spells, so every testcase has exactly
/// one execution to resolve to.
/// </summary>
public sealed partial class LargeReportBudgetTests
{
    /// <summary>Generates the pinned report and returns its path; the caller's temp directory owns the file.</summary>
    /// <param name="directory">The directory the generator writes <c>input.xml</c> into.</param>
    /// <returns>The report's path, after the bytes matched the fixture's own pin.</returns>
    private static string GenerateReport(string directory)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(FixtureLoader.RepoRoot, "scripts", "dev", "gen-large-report", "gen-large-report.mjs"));
        start.ArgumentList.Add("--out-dir");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--tests=" + PlannedExecutions.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--seed=" + Seed.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("node could not be started, so the fixture report cannot be generated");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, stderr);

        var summary = JsonDocument.Parse(stdout).RootElement;
        summary.GetProperty("tests").GetInt32().ShouldBe(PlannedExecutions);
        summary.GetProperty("sha256").GetString().ShouldBe(Expected.GetProperty("full").GetProperty("sha256").GetString());
        summary.GetProperty("bytes").GetInt64().ShouldBe(Expected.GetProperty("full").GetProperty("bytes").GetInt64());

        return Path.Combine(directory, "input.xml");
    }

    /// <summary>
    /// The plan: one case and revision per testcase identity, plus the planned execution that identity resolves to.
    /// One statement writes all three tables, so 100 000 rows cost three hash joins and no per-row round trip.
    /// </summary>
    /// <param name="world">The tenant, project and run the plan belongs to.</param>
    /// <param name="reportPath">The generated report whose testcases are the plan's identities.</param>
    private async Task SeedPlanAsync(World world, string reportPath)
    {
        var identities = Identities(reportPath);

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var inserted = await scope.ExecuteAsync(
            """
            with names as (
              select (entry ->> 0) as classname, (entry ->> 1) as name
                from jsonb_array_elements(@names::jsonb) as entry
            ), cases as (
              insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
              select gen_random_uuid(), @org_id, @project_id, gen_random_uuid(), n.name, 'pytest', 'abcdef0'
                from names n
              returning id, name
            ), revisions as (
              insert into public.test_revision
                (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash)
              select gen_random_uuid(), @org_id, c.id,
                     'sha256:' || encode(sha256(convert_to(c.id::text, 'UTF8')), 'hex'),
                     'abcdef0',
                     replace(n.classname, '.', '/') || '.py',
                     'pytest',
                     'sha256:' || encode(sha256(convert_to('content|' || c.id::text, 'UTF8')), 'hex'),
                     'sha256:' || encode(sha256(convert_to('assertion|' || c.id::text, 'UTF8')), 'hex')
                from cases c
                join names n on n.name = c.name
              returning id, case_id
            )
            insert into public.planned_execution
              (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
            select gen_random_uuid(), @org_id, @run_id,
                   encode(sha256(convert_to('execution|' || r.case_id::text, 'UTF8')), 'hex'),
                   r.id,
                   'sha256:' || encode(sha256(convert_to('variant|' || r.case_id::text, 'UTF8')), 'hex'),
                   'staging', 'shard-1'
              from revisions r
            """,
            ("names", identities), ("org_id", world.Org), ("project_id", world.Project), ("run_id", world.RunId));

        inserted.ShouldBe(PlannedExecutions);
        await scope.CommitAsync();
    }

    /// <summary>
    /// One <c>ready</c> <c>raw_report</c> artifact whose final object the store really holds, in the slot of one of the
    /// plan's own executions: the report is the evidence of a run whose plan this test wrote, not a second plan.
    /// </summary>
    /// <param name="world">The tenant and run the artifact belongs to.</param>
    /// <param name="reportPath">The generated report's bytes.</param>
    /// <returns>The artifact's id and object key, for the cleanup the caller owes the store.</returns>
    private async Task<Artifact> SeedArtifactAsync(World world, string reportPath)
    {
        var body = await File.ReadAllBytesAsync(reportPath, Token);
        var artifactId = Guid.CreateVersion7();
        var executionKey = await SlotKeyAsync(world);
        var objectKey = string.Create(CultureInfo.InvariantCulture,
            $"org/{world.Org:D}/project/{world.Project:D}/run/{world.RunId:D}/attempt/{executionKey}/{artifactId:D}");
        var checksum = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

        await _store.PutAsync(objectKey, body);
        try
        {
            await _fixture.ExecuteAsync(
                world.Database,
                "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, "
                + "object_key, media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) values "
                + $"('{artifactId:D}', '{world.Org:D}', '{world.Project:D}', '{world.RunId:D}', '{executionKey}', 1, "
                + $"'{ArtifactKind.RawReport}', '{objectKey}', '{XmlType}', '{checksum}', 'normal', 'pending', 'ready', "
                + $"{body.LongLength.ToString(CultureInfo.InvariantCulture)}, null)");
        }
        catch
        {
            await _store.DeleteAsync(objectKey);
            throw;
        }

        return new Artifact(artifactId, objectKey);
    }

    /// <summary>One of the plan's own execution keys, so the artifact's slot belongs to a live execution.</summary>
    /// <param name="world">The tenant and run whose plan was just written.</param>
    /// <returns>The first key in key order, which is stable for one plan.</returns>
    private async Task<string> SlotKeyAsync(World world) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select execution_key from public.planned_execution where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' order by execution_key limit 1");

    /// <summary>The report's testcase identities as one JSON array of <c>[classname, name]</c> pairs.</summary>
    /// <param name="reportPath">The generated report.</param>
    /// <returns>The identities, ready to bind as <c>::jsonb</c>.</returns>
    private static string Identities(string reportPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        var count = 0;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            using var reader = XmlReader.Create(reportPath, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.LocalName, "testcase", StringComparison.Ordinal))
                {
                    continue;
                }

                writer.WriteStartArray();
                writer.WriteStringValue(reader.GetAttribute("classname") ?? string.Empty);
                writer.WriteStringValue(reader.GetAttribute("name") ?? string.Empty);
                writer.WriteEndArray();
                count++;
            }

            writer.WriteEndArray();
        }

        count.ShouldBe(PlannedExecutions, "the generated report must carry exactly the plan's testcase count");

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
