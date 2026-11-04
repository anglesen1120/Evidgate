// The bounds and the write ordering of the audited kind-policy override route. Every request is a member principal
// inside the C2 pipeline, and every assertion reads the committed row back as the migration role.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c> bounds its body on the bytes it actually reads, caps a
/// kind's size at what the store can hand to a scan or a parse, and records the sensitivity its write replaces,
/// including a kind with no row yet and a write that queued behind another of the same kind.
/// </summary>
public sealed partial class EvidenceSensitivityTests
{
    /// <summary>
    /// A body of exactly the route's cap is accepted whether the client declares its length or streams it: the limit
    /// is inclusive, and the second write (streamed, chunked) proves the cap counts bytes instead of trusting a
    /// header.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_BodyExactlyAtTheBodyLimit_IsAccepted()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var declared = await member.Client.SendRawAsync(
            HttpMethod.Put,
            PolicyUrl(world.Project, ArtifactKind.Trace),
            member.Caller,
            PaddedPolicyBody(ArtifactSensitivity.Normal, UploadUrlLimits.MaxBodyBytes));
        declared.Status.ShouldBe(HttpStatusCode.OK, declared.ErrorMessage);

        var streamed = await SendLengthlessAsync(
            member.Client,
            member.Caller,
            PolicyUrl(world.Project, ArtifactKind.Trace),
            Encoding.UTF8.GetBytes(PaddedPolicyBody(ArtifactSensitivity.Sensitive, UploadUrlLimits.MaxBodyBytes)));
        streamed.Status.ShouldBe(HttpStatusCode.OK, streamed.ErrorMessage);

        (await PolicyRowsAsync(world, world.Project)).ShouldBe(["trace|2048|sensitive|true|false"]);
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).Count.ShouldBe(2);
    }

    /// <summary>
    /// A chunked body — no <c>Content-Length</c> a header check could see — one byte over the cap is the same 413
    /// <c>payload_too_large</c>, and neither the row nor its audit entry exists afterwards.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_LengthlessBodyOverTheBodyLimit_413AndNothingWritten()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var answer = await SendLengthlessAsync(
            member.Client,
            member.Caller,
            PolicyUrl(world.Project, ArtifactKind.Trace),
            Encoding.UTF8.GetBytes(PaddedPolicyBody(ArtifactSensitivity.Normal, UploadUrlLimits.MaxBodyBytes + 1)));

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("payload_too_large");
        answer.Body.GetRawText().ShouldNotContain("application/zip", Case.Sensitive, "the refusal echoes nothing of the body");
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).ShouldBeEmpty();
    }

    /// <summary>
    /// A cap at the storage ceiling is stored: it is the largest object one scan or parse read can buffer, so it is
    /// a policy that can still be honoured.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_MaxBytesAtTheStorageCeiling_IsStored()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put,
            PolicyUrl(world.Project, ArtifactKind.RawReport),
            member.Caller,
            PolicyBodyWithMaxBytes(ObjectStorageLimits.MaxObjectBytes));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("max_bytes").GetInt64().ShouldBe(ObjectStorageLimits.MaxObjectBytes);
        (await PolicyRowsAsync(world, world.Project))
            .ShouldBe([$"raw_report|{ObjectStorageLimits.MaxObjectBytes}|normal|true|false"]);
    }

    /// <summary>
    /// One byte over the storage ceiling is refused 422 <c>out_of_range</c> before any statement: a stored cap that
    /// high would be a row whose every scan and parse read fails.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_MaxBytesOverTheStorageCeiling_422AndNothingWritten()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put,
            PolicyUrl(world.Project, ArtifactKind.RawReport),
            member.Caller,
            PolicyBodyWithMaxBytes(ObjectStorageLimits.MaxObjectBytes + 1));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        Reasons(answer).ShouldContain("out_of_range");
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).ShouldBeEmpty();
    }

    /// <summary>
    /// A project with no row for the kind was running on the kind's static default, so that default is the previous
    /// sensitivity the lowering records — not an empty value an operator cannot interpret.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_LowerTraceToNormalWithNoRow_RecordsTheKindsDefaultAsPrevious()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put,
            PolicyUrl(world.Project, ArtifactKind.Trace),
            member.Caller,
            PolicyBody(ArtifactSensitivity.Normal));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var audit = (await AuditRowsAsync(world, "artifact_kind_policy.set", $"{world.Project:D}:{ArtifactKind.Trace}"))
            .ShouldHaveSingleItem();
        audit.PreviousDefaultSensitivity.ShouldBe(
            ArtifactSensitivity.Sensitive, "a kind that defaults to sensitive is what the first write replaced");
        audit.Status.ShouldBe(ArtifactSensitivity.Normal);
        (await PolicyRowsAsync(world, world.Project)).ShouldBe(["trace|2048|normal|true|false"]);
    }

    /// <summary>
    /// Two writers of one kind serialize on the kind's key: the queued one reads its previous value AFTER the lock,
    /// so it records the value the writer it queued behind committed. The holder changes the row inside its
    /// transaction, which the queued read cannot see until the commit — so a previous read taken before the lock
    /// would record <c>sensitive</c> and break the chain.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_QueuedWriter_RecordsTheCommittedPreviousValue()
    {
        await using var world = await StartAsync();
        await SeedPolicyAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive);
        var member = world.MemberClient(world.Founder);
        var key = $"{world.Org:D}:{world.Project:D}:{ArtifactKind.Trace}";

        await using var gate = await TenantScope.BeginAsync(
            _fixture.OwnerConnectionString(world.Database), world.Org, world.Project);

        // The same key and the same expression the handler takes, so the test would fail if the two drift.
        (await gate.ScalarAsync<long>(
            "select count(*) from (select pg_advisory_xact_lock(hashtext('evidgate.artifact_kind_policy'), "
            + "hashtext(@key))) as taken",
            ("key", key))).ShouldBe(1L);
        await gate.ExecuteAsync(
            "update public.artifact_kind_policy set default_sensitivity = @sensitivity "
            + "where org_id = @org_id and project_id = @project_id and kind = @kind",
            ("sensitivity", ArtifactSensitivity.Normal),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("kind", ArtifactKind.Trace));

        var queued = member.Client.SendRawAsync(
            HttpMethod.Put,
            PolicyUrl(world.Project, ArtifactKind.Trace),
            member.Caller,
            PolicyBody(ArtifactSensitivity.Sensitive));

        await WaitForKindLockWaitAsync(world, key);
        await gate.CommitAsync();

        var answer = await queued.WaitAsync(TimeSpan.FromSeconds(60), Token);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var audit = (await AuditRowsAsync(world, "artifact_kind_policy.set", $"{world.Project:D}:{ArtifactKind.Trace}"))
            .ShouldHaveSingleItem();
        audit.PreviousDefaultSensitivity.ShouldBe(
            ArtifactSensitivity.Normal, "the write that queued behind the commit reads the committed row");
        audit.Status.ShouldBe(ArtifactSensitivity.Sensitive);
    }

    /// <summary>
    /// Blocks until a backend of this test's database observably waits on the kind lock, so the ordering is entered
    /// deterministically instead of hoped for, and fails with the reason after 30 s.
    /// </summary>
    /// <param name="world">The test's database.</param>
    /// <param name="key">The kind's lock key, as the handler spells it.</param>
    private async Task WaitForKindLockWaitAsync(World world, string key)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                world.Database,
                "select count(*) from pg_locks "
                + "where locktype = 'advisory' and not granted and objsubid = 2 "
                + "and database = (select oid from pg_database where datname = current_database()) "
                + "and classid::bigint = (hashtext('evidgate.artifact_kind_policy')::bigint & 4294967295) "
                + $"and objid::bigint = (hashtext('{key}')::bigint & 4294967295)");

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, Token);
        }

        throw new InvalidOperationException("the queued write never waited on the kind lock within 30 s");
    }

    /// <summary>
    /// Sends one policy body streamed from a source whose length cannot be known, so no <c>Content-Length</c>
    /// travels: only the endpoint's own counting can bound it.
    /// </summary>
    /// <param name="client">The host's client.</param>
    /// <param name="caller">The caller headers of the request.</param>
    /// <param name="url">The policy route.</param>
    /// <param name="body">The bytes to send.</param>
    /// <returns>The parsed answer.</returns>
    private static async Task<ApiAnswer> SendLengthlessAsync(
        HttpClient client,
        Action<HttpRequestHeaders> caller,
        string url,
        byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        caller(request.Headers);
        using var content = new StreamContent(new NoLengthStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        using var response = await client.SendAsync(request, Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>A valid policy body of exactly <paramref name="totalBytes"/> bytes: legal JSON plus trailing spaces.</summary>
    /// <param name="sensitivity">The sensitivity the body asks for.</param>
    /// <param name="totalBytes">The exact byte count to reach.</param>
    /// <returns>The padded body.</returns>
    private static string PaddedPolicyBody(string sensitivity, int totalBytes)
    {
        var json = PolicyBody(sensitivity);
        var length = Encoding.UTF8.GetByteCount(json);

        length.ShouldBeLessThan(totalBytes, "the padding must leave room for the whole body");

        return json + new string(' ', totalBytes - length);
    }

    /// <summary>The complete policy body with one <c>max_bytes</c> value.</summary>
    /// <param name="maxBytes">The size cap to ask for.</param>
    /// <returns>The body.</returns>
    private static string PolicyBodyWithMaxBytes(long maxBytes) =>
        $$"""
        {"max_bytes":{{maxBytes}},"media_types":["application/zip"],"default_sensitivity":"normal","capture_enabled":true,"reject_on_secret":false}
        """;

    /// <summary>The <c>error.details[].reason</c> codes of a refused answer.</summary>
    /// <param name="answer">The refusal.</param>
    /// <returns>The reason codes in order.</returns>
    private static IReadOnlyList<string> Reasons(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("reason").GetString() ?? string.Empty)];

    /// <summary>
    /// A read-only stream of <paramref name="bytes"/> that reports no length and cannot seek, so its content is sent
    /// without a <c>Content-Length</c>.
    /// </summary>
    /// <param name="bytes">The body bytes.</param>
    private sealed class NoLengthStream(byte[] bytes) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var taken = Math.Min(count, bytes.Length - position);
            bytes.AsSpan(position, taken).CopyTo(buffer.AsSpan(offset, taken));
            position += taken;

            return taken;
        }

        public override void Flush()
        {
            // Read-only.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
