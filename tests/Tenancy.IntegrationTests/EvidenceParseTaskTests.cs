// 06.3.6-T1.4 (AC3, AC8 second half) on the path the criteria name: the `artifact_parse` task handler over the real
// Api composition, a real PostgreSQL and a real SeaweedFS container. The runner is the one seam the test replaces, so
// the mapping from a failed child run to a `rejected` reason, the guarded write, and the no-op paths are the
// production ones. Expectations come from the plan (06.3.6 §2 AC3/AC8, §6, §7, §8) and the coordinator decision that
// fixes the exit-code mapping, not from the current behaviour.
using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>artifact_parse</c> task handler (06.3.6-T1.4) against one migrated database and one SeaweedFS container: a
/// failed run becomes <c>rejected</c> with the mapped <c>parse_*</c> code, a successful run and every no-op path write
/// nothing, an infrastructure failure is retried rather than judged, a cancellation is never a verdict, and a
/// rejection is written once whatever the number of attempts.
/// </summary>
/// <remarks>
/// The runner is replaced by <see cref="StubParseRunner"/> — the one seam this task adds — so the test drives the exact
/// <see cref="ParseRunResult"/> shapes the real child produces (measured in 06.3.6-T1.3) without spawning a process.
/// Everything else is production: the tenant scope, the EF write model, the guarded <c>UPDATE</c>, the store read and
/// the object storage adapter.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EvidenceParseTaskTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>The task's wire name (06.3.6 §5).</summary>
    private const string ParseTask = "artifact_parse";

    /// <summary>The media type of the report the handler reads (XML family → the <c>Xml</c> arm).</summary>
    private const string XmlType = "application/xml";

    /// <summary>A well-formed execution key: sha256 hex (00 §7), the key the artifact's pair carries.</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A small well-formed report body: the handler never interprets it, the child would.</summary>
    private static readonly byte[] ReportBody =
        "<report><testcase name=\"checkout\" outcome=\"passed\"/></report>"u8.ToArray();

    private static readonly string ReportChecksum = "sha256:" + Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(ReportBody));

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record World(
        TenancyApiFactory Host,
        string Database,
        Guid Org,
        Guid Project,
        Guid RunId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    /// <summary>One seeded row: a <c>raw_report</c> artifact whose bytes the store really holds.</summary>
    private sealed record Seeded(Guid ArtifactId, string ObjectKey);

    /// <summary>
    /// Every failed-run shape the child can produce (06.3.6-T1.3 measurements) and the reason the mapper gives it.
    /// The failure kind travels as its name because <see cref="ParseRunFailureKind"/> is internal: a public theory may
    /// only close over public types, and the name keeps the test case readable in a report.
    /// <para>
    /// <b>The two limit signals.</b> The child sets <c>RLIMIT_CPU</c> with soft equal to its seconds and hard equal to
    /// seconds + 1, so the kernel's <c>SIGXCPU</c> reaches it first and the run ends at <c>128 + 24 = 152</c> —
    /// <see cref="ParseRunRejection.Timeout"/>. Exit 137 (<c>128 + SIGKILL</c>) is therefore never the CPU limit: it is
    /// the kernel OOM killer or an outside <c>SIGKILL</c>, and a parent cannot read the child's CPU time after it was
    /// reaped, so it stays <see cref="ParseRunRejection.Crashed"/> rather than being guessed as a limit that was
    /// applied. A wall-clock expiry is attributed before the exit code is consulted, so the runner's own kill is
    /// <see cref="ParseRunRejection.Timeout"/> whatever the code.
    /// </para>
    /// </summary>
    public static TheoryData<string, int?, string> FailureMapping => new()
    {
        { nameof(ParseRunFailureKind.ExitedAbnormally), 4, ParseRunRejection.Oom },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 152, ParseRunRejection.Timeout },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 137, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 3, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.WallClockExceeded), 137, ParseRunRejection.Timeout },
        { nameof(ParseRunFailureKind.OutputBytesExceeded), 0, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.OutputRecordsExceeded), 0, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.MalformedOutput), 0, ParseRunRejection.Crashed },
    };

    /// <summary>
    /// The three tenant-visible reasons are the domain vocabulary and the mapper's constants, and they are the same
    /// three strings (06.3.6 AC3): a rename in one place cannot silently change the wire value.
    /// </summary>
    [Fact]
    public void ParseReasons_Are_The_Domain_Failure_Codes()
    {
        ParseRunRejection.Oom.ShouldBe(ArtifactFailureCode.ParseOom);
        ParseRunRejection.Timeout.ShouldBe(ArtifactFailureCode.ParseTimeout);
        ParseRunRejection.Crashed.ShouldBe(ArtifactFailureCode.ParseCrashed);
        ArtifactFailureCode.ParseOom.ShouldBe("parse_oom");
        ArtifactFailureCode.ParseTimeout.ShouldBe("parse_timeout");
        ArtifactFailureCode.ParseCrashed.ShouldBe("parse_crashed");
    }

    /// <summary>
    /// AC3: every failed shape ends as <c>rejected</c> with the mapped reason, exactly one version increment per write,
    /// and the row's bytes/scan state are preserved (the artifact keeps what it declared; only the state and the code
    /// change).
    /// </summary>
    /// <param name="failure">The name of the <see cref="ParseRunFailureKind"/> the runner reports.</param>
    /// <param name="exitCode">The child exit code the runner reports.</param>
    /// <param name="reason">The reason the row must carry.</param>
    [Theory]
    [MemberData(nameof(FailureMapping))]
    public async Task Parse_Failed_Run_Is_Rejected_With_The_Mapped_Reason(
        string failure,
        int? exitCode,
        string reason)
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(Enum.Parse<ParseRunFailureKind>(failure), exitCode, 1024, 3, "boom"),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(reason);
        (await ColumnAsync(world, seeded.ArtifactId, "bytes")).ShouldBe(
            ReportBody.LongLength.ToString(CultureInfo.InvariantCulture), "the verified length is kept");
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(
            versionBefore + 1, "one guarded write, one version increment");
        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody, "the child was handed the stored bytes");
    }

    /// <summary>
    /// A successful run persists nothing — the parity the coordinator asked for: no in-process consumer stores parsed
    /// artifact output today (the <c>IParsing</c> callers return the profile's value to their own caller), so a
    /// successful out-of-process parse must not invent a representation either.
    /// </summary>
    [Fact]
    public async Task Parse_Successful_Run_Writes_Nothing()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded([], ChildExitCode: 0, OutputBytes: 64, OutputRecords: 0),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);
        var updatedBefore = await ColumnAsync(world, seeded.ArtifactId, "updated_at");

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(versionBefore, "nothing was written");
        (await ColumnAsync(world, seeded.ArtifactId, "updated_at")).ShouldBe(updatedBefore);
        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody);
    }

    /// <summary>
    /// A spawn failure means no child existed to judge the artifact: the handler throws so the queue retries with
    /// backoff (06.3.6 §6), and the row keeps its state — a limit verdict is never invented for a host that never ran.
    /// </summary>
    [Fact]
    public async Task Parse_SpawnFailure_Throws_And_Leaves_The_Artifact_Parseable()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.SpawnFailed, ChildExitCode: null, 0, 0),
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => InvokeAsync(world, seeded.ArtifactId, runner));

        exception.Message.ShouldNotContain(seeded.ObjectKey, Case.Sensitive, "no key reaches a task's last_error");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// AC8's "parsed once": an artifact that already carries a verdict is not parsed again, so a replayed row costs no
    /// child process and no statement.
    /// </summary>
    [Fact]
    public async Task Parse_Already_Rejected_Is_A_NoOp_Without_Starting_The_Child()
    {
        await using var world = await StartAsync();
        var seeded = await SeedAsync(world, state: ArtifactState.Rejected, failureCode: ArtifactFailureCode.ParseOom);
        var runner = new StubParseRunner();

        await InvokeAsync(world, seeded.ArtifactId, runner);

        runner.Calls.ShouldBe(0, "a decided artifact is never parsed again");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.ParseOom);
    }

    /// <summary>
    /// A cancellation is the caller's (lease loss, shutdown), never a parse verdict: the runner's own cancellation
    /// propagates, and the artifact keeps its state for the redelivered attempt.
    /// </summary>
    [Fact]
    public async Task Parse_Cancellation_Is_Not_A_Verdict()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner { Throws = new OperationCanceledException("lease lost") };

        await Should.ThrowAsync<OperationCanceledException>(() => InvokeAsync(world, seeded.ArtifactId, runner));

        runner.Calls.ShouldBe(1, "the runner was started and then cancelled");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// A payload naming another tenant's artifact reaches nothing: RLS makes the row invisible, so the attempt changes
    /// nothing and the other tenant's artifact stays exactly as it was (06.3.6 §8 "one artifact's bytes only").
    /// </summary>
    [Fact]
    public async Task Parse_Payload_Of_Another_Tenant_Is_Unreachable()
    {
        await using var world = await StartAsync();
        var other = await CreateOtherWorldAsync("globex");
        var foreign = await SeedReadyAsync(other);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 4, 0, 0),
        };

        await InvokeAsync(world, foreign.ArtifactId, runner);

        runner.Calls.ShouldBe(0, "another tenant's artifact is not a row in this scope");
        (await ColumnAsync(other, foreign.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(other, foreign.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// AC8: two attempts over the same rejected artifact write once. The second attempt sees the stored verdict and
    /// starts nothing (and the guarded <c>UPDATE</c> would match no row even if it did), so the version increments a
    /// single time and the code is the first attempt's.
    /// </summary>
    [Fact]
    public async Task Parse_Replayed_Attempt_Writes_Once()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);
        var afterFirst = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);

        afterFirst.ShouldBe(versionBefore + 1);
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(afterFirst, "the replay changed nothing");
        runner.Calls.ShouldBe(1, "the replay never started a second child");
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ParseRunRejection.Timeout);
    }

    /// <summary>
    /// The coordinator's freeze check: a freeze run after the rejection does not count the artifact. Before the verdict
    /// the ready artifact is reported (as an orphan: this fixture has no <c>case_attempt</c> for its pair), and after it
    /// the same call reports nothing at all — missing evidence never reads as complete, and rejected bytes are never
    /// evidence (00 §8.4, 06.5.1).
    /// </summary>
    [Fact]
    public async Task Parse_Rejected_Artifact_Is_Not_Counted_By_A_Later_Freeze()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);

        var before = await FreezeAsync(world);
        before.Ready.Count.ShouldBe(0);
        before.Orphans.Count.ShouldBe(1, "before the verdict the artifact is still ready evidence");

        await InvokeAsync(
            world,
            seeded.ArtifactId,
            new StubParseRunner { Result = new ParseRunFailed(ParseRunFailureKind.MalformedOutput, 0, 0, 0) });

        var after = await FreezeAsync(world);
        after.Ready.ShouldBeEmpty();
        after.Orphans.ShouldBeEmpty("a rejected artifact is not evidence any more, so it is not in the frozen set");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, "acme");
            project = await CreateProjectAsync(setup, user, org, "checkout");
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-parse-1"),
            _store.Configuration());

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        return new World(host, database, org, project, runId);
    }

    /// <summary>A second org with a project and one ready artifact, for the cross-tenant case.</summary>
    private async Task<World> CreateOtherWorldAsync(string slug)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, slug);
            project = await CreateProjectAsync(setup, user, org, slug + "-project");
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-parse-2"),
            _store.Configuration());

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        return new World(host, database, org, project, runId);
    }

    /// <summary>One <c>ready</c> <c>raw_report</c> artifact whose final object the store really holds.</summary>
    private Task<Seeded> SeedReadyAsync(World world) => SeedAsync(world, ArtifactState.Ready, failureCode: null);

    /// <summary>
    /// Inserts one artifact row (as the migration role, the same way the 06.1.2-T2 suite seeds the rows a caller must
    /// not resolve) and puts the report's bytes at its final key, so the handler's read is a real store read.
    /// </summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="state">The row's <c>artifact.state</c>.</param>
    /// <param name="failureCode">The reason code a rejected row carries.</param>
    private async Task<Seeded> SeedAsync(World world, string state, string? failureCode)
    {
        var artifactId = Guid.CreateVersion7();

        // The final-key shape the table's own check constraint states (0028:81-84), so the row is the real promotion
        // target rather than a key only this test agrees with.
        var objectKey = $"org/{world.Org:D}/project/{world.Project:D}/run/{world.RunId:D}/attempt/"
            + $"{ExecutionKey}/1/{artifactId:D}";

        await _store.PutAsync(objectKey, ReportBody);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) "
            + $"values ('{artifactId:D}', '{world.Org:D}', '{world.Project:D}', '{world.RunId:D}', "
            + $"'{ExecutionKey}', 1, '{ArtifactKind.RawReport}', '{objectKey}', '{XmlType}', "
            + $"'{ReportChecksum}', 'normal', 'pending', '{state}', {ReportBody.LongLength}, "
            + (failureCode is null ? "null" : $"'{failureCode}'") + ")");

        return new Seeded(artifactId, objectKey);
    }

    /// <summary>
    /// The call the task consumer makes, minus the queue: the production handler over this tenant's scope, invoked
    /// through the same <c>TaskHandlerAdapter</c> the consumer uses, so the payload binding is the consumer's.
    /// </summary>
    private static async Task InvokeAsync(World world, Guid artifactId, StubParseRunner runner, CancellationToken? cancellationToken = null)
    {
        var token = cancellationToken ?? Token;

        var handler = new ParseArtifactTaskHandler(
            world.Host.Services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            world.Host.Services.GetRequiredService<IObjectStorage>(),
            runner,
            NullLogger<ParseArtifactTaskHandler>.Instance);

        var invoker = new TaskHandlerAdapter<ParseArtifactPayload>(handler);
        invoker.TaskType.ShouldBe(ParseTask);

        var payload = JsonSerializer.Serialize(new { artifact_id = artifactId });
        var factory = world.Host.Services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: token);
        await invoker.InvokeAsync(payload, scope, token);
        await scope.CommitAsync(token);
    }

    /// <summary>One freeze of the world's run with no requirements, through the production service.</summary>
    private static async Task<FreezeResult> FreezeAsync(World world)
    {
        using var services = world.Host.Services.CreateScope();
        var freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        var factory = world.Host.Services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: Token);
        var result = await freeze.FreezeAsync(new FreezeRequest(world.RunId, []), Token);
        await scope.CommitAsync(Token);

        return result;
    }

    private Task<string?> ColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select {column}::text from public.artifact where id = '{artifactId:D}'");

    private Task<int> VersionAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select version from public.artifact where id = '{artifactId:D}'");
}

/// <summary>
/// The one seam the handler adds, replaced: a runner whose answer the test states, which records every call and the
/// bytes it was handed, and which can block until the caller's cancellation to model a parse in flight.
/// </summary>
internal sealed class StubParseRunner : IParseRunner
{
    /// <summary>The result every call answers; <see langword="null"/> unless <see cref="Throws"/> is set.</summary>
    public ParseRunResult? Result { get; init; }

    /// <summary>
    /// The exception the run raises instead of answering — the shape a cancelled run has, where the runner has
    /// already tree-killed its child and rethrows <see cref="OperationCanceledException"/>.
    /// </summary>
    public Exception? Throws { get; init; }

    /// <summary>How many runs were started.</summary>
    public int Calls { get; private set; }

    /// <summary>The input bytes of every run, in order: proof the handler handed over the stored report.</summary>
    public List<byte[]> Inputs { get; } = [];

    /// <inheritdoc />
    public async Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;

        using var buffer = new MemoryStream();
        await request.Input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inputs.Add(buffer.ToArray());

        if (Throws is not null)
        {
            throw Throws;
        }

        if (Result is null)
        {
            throw new InvalidOperationException("the stub has no answer for this run");
        }

        return Result;
    }
}
