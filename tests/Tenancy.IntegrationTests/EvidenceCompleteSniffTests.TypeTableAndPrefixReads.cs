using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceCompleteSniffTests
{
    // ------------------------------------- AC5: match accepts; a type outside the table rejects (fail closed)

    /// <summary>06.3.1 AC5: <c>text/html</c> declared with valid markup — the sniff matches, so it is <c>ready</c>.</summary>
    [Fact]
    public async Task Complete_DeclaredHtmlWithMarkupPrefix_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, HtmlType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, HtmlBody, HtmlType);

        world.Host.Statements.Clear();
        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(HtmlBody.LongLength);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBeNull();
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(HtmlBody);

        // P5 (00 §5.7): the sniff's ReadPrefixAsync runs with no transaction open and is a network call, never
        // SQL — the completion stays within the same bounded statement count Complete_BoundedStatements derives.
        var statements = world.Host.Statements.Snapshot();
        statements.Count.ShouldBeGreaterThan(0);
        statements.Count.ShouldBeLessThanOrEqualTo(16);
    }

    /// <summary>06.3.1 AC5: <c>application/xml</c> declared with a valid XML prefix — the sniff matches.</summary>
    [Fact]
    public async Task Complete_DeclaredXmlWithXmlPrefix_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(XmlBody);
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5: <c>image/png</c> declared with the PNG signature stored — the sniff matches, so it is
    /// <c>ready</c>. The table is no longer the XML/HTML/JSON families only.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredPngWithPngSignature_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, PngType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, PngBody, PngType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("failure_code").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(PngBody);
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5: <c>application/zip</c> is now sniffed, so non-zip bytes under that declaration are a
    /// committed 200 <c>rejected</c>/<c>media_type_mismatch</c> — never <c>ready</c>, never promoted.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredZipWithNonZipBytes_RejectedMediaTypeMismatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, ZipType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, ZipType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a sniff rejection is not a staging delete");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5 fail closed: <c>application/octet-stream</c> is accepted by the policy but the sniff table
    /// has no signature for it, so its bytes are a <c>Mismatch</c> — the artifact is <c>rejected</c> with
    /// <c>media_type_mismatch</c> and never becomes evidence. This is the T1 <c>NotSniffed</c> fail-open closed.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredTypeOutsideTheSniffTable_RejectedMediaTypeMismatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, OutOfTableType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, OutOfTableType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a sniff rejection is not a staging delete");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    // ------------------------ AC5/T2.2: every declared type is read (fail closed), staging then final

    /// <summary>
    /// 06.3.1-T2.2: <c>application/zip</c> is sniffed now, so the completion reads the staging prefix before the
    /// copy — and the mismatching bytes are rejected there, before the promotion. The reads are counted by a
    /// decorator over the real adapter.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredZip_IssuesAStagingPrefixReadAndRejects()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, ZipType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, ZipType);

        var storage = new FaultInjectingObjectStorage();
        await using var host = world.Host.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));
        using var client = host.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");

        // Only the staging read runs: the mismatch refuses the completion before the promotion, so the final
        // object is never created and its prefix is never read.
        storage.PrefixReads.ShouldBe(1, "the staging sniff refuses the completion before the promotion");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1-T2.2 fail closed without a read: a declared type the sniff table has no signature for can never be
    /// verified, so the completion refuses it (<c>media_type_mismatch</c>) before the promotion and issues ZERO
    /// <c>ReadPrefixAsync</c> calls — there is no prefix it could use (00 §5.7 P1). The artifact is never
    /// promoted to the final key.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredTypeOutsideTheSniffTable_IssuesNoPrefixReadAndRejects()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, OutOfTableType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, OutOfTableType);

        var storage = new FaultInjectingObjectStorage();
        await using var host = world.Host.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));
        using var client = host.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        storage.PrefixReads.ShouldBe(0, "an unsigned declared type is refused without any prefix read");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5: a declared type the profile sniffs and that MATCHES (<c>application/xml</c>) reads the
    /// prefix twice — once on the staging object before the copy and once on the final object after it.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredXmlFamily_IssuesTwoPrefixReads()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        var storage = new FaultInjectingObjectStorage();
        await using var host = world.Host.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));
        using var client = host.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        storage.PrefixReads.ShouldBe(2, "a sniffed type reads the staging prefix before the copy and the final prefix after it");
    }

    // ------------------------------------------------------------------ P5: no storage call before the key check

    /// <summary>
    /// 06.3.1-T1 ordering (T92, 00 §5.7 P5): the endpoint validates the <c>Idempotency-Key</c> before the
    /// verifier runs, so a missing key answers 400 <c>request_invalid</c> without a single storage read or
    /// write — no object is promoted, the staging object survives, the row is untouched, and the verifier's
    /// row-facts read never reaches the database (so <c>ReadPrefixAsync</c> is never reached).
    /// </summary>
    [Fact]
    public async Task Complete_MissingIdempotencyKey_400_BeforeAnyStorageRead()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        // A matching object is staged, so only the missing key can refuse the completion.
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        world.Host.Statements.Clear();
        var answer = await CompleteAsync(world, artifactId);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");

        // No storage call: the verifier's promotion never ran, so the final key is empty and the staging
        // object is untouched — and no statement reads the row facts the verifier would need.
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("the key is refused before the verifier promotes bytes");
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the refusal happens before any storage call");
        world.Host.Statements.Snapshot()
            .Any(statement => statement.Contains("artifact_kind_policy", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse("the verifier's row-facts read must not run for a refused key");
    }
}
