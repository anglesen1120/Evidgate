using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA pass over 06.1.1-T2 (artifact key minting and client-key rejection), written by a reviewer who did not write
/// the code. Negative, boundary and exploratory cases the existing <c>EvidenceCreateArtifactRecordTests</c> does not
/// cover: every refusal in its exact code, the validator's exact-form edges, the per-kind sensitivity fallback,
/// policy-row isolation by project and by org, the persisted row shape against 0028, and the tenant/permission
/// refusals of the C2 pipeline. Real pipeline and real PostgreSQL, as in the existing class.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCreateArtifactRecordQaTests
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ZipType = "application/zip";

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceCreateArtifactRecordQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    private static RequestPrincipal Writer(Guid? projectScope = null) =>
        RequestPrincipal.Service(projectScope, [Permissions.Artifact.Write]);

    private static CreateArtifactRecord Command(
        Guid? runId = null,
        string? clientKey = null,
        string kind = ArtifactKind.Trace,
        string checksum = Checksum,
        long bytes = 1024,
        string executionKey = ExecutionKey,
        int attemptNo = 1,
        string mediaType = ZipType) =>
        new(runId ?? Guid.CreateVersion7(), executionKey, attemptNo, kind, mediaType, bytes, checksum, clientKey);

    /// <summary>One mint through the real Mediator pipeline with the given published tenant and principal.</summary>
    private static async Task<Result<ArtifactRecord>> SendAsync(World world, Guid? publishedProject, RequestPrincipal principal, CreateArtifactRecord command)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, publishedProject);
        using var published = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(principal);

        return await provider.GetRequiredService<IMediator>().Send(command, TestContext.Current.CancellationToken);
    }

    private static Task<Result<ArtifactRecord>> MintAsync(World world, CreateArtifactRecord command) =>
        SendAsync(world, world.Project, Writer(), command);

    private static void ShouldBeRefused(Result<ArtifactRecord> result, int status, string? code, string label)
    {
        result.IsSuccess.ShouldBeFalse(label);
        result.Error.ShouldNotBeNull(label);
        result.Error.Status.ShouldBe(status, label);

        if (code is not null)
        {
            result.Error.Code.ShouldBe(code, label);
        }
    }

    private async Task InsertKindPolicyAsync(string database, Guid org, Guid project, string kind, string sensitivity)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', '{project}', '{kind}', 1024, array['application/zip'], '{sensitivity}')"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    private Task<long> RowsAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.artifact where org_id = '{world.Org:D}'");

    /// <summary>One committed run row for <paramref name="org"/> (0031's composite key needs it on every write).</summary>
    private async Task<Guid> CreateRunAsync(World world, Guid org) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

    private Task<string> ColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string>(world.Database, $"select {column} from public.artifact where id = '{artifactId}'");

    // ------------------------------------------------------------------ AC1: every client-key shape is refused

    [Fact]
    public async Task ClientKey_OfAnyShape_IsRefused_WithObjectKeyNotAllowed_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOrgAsync(world.Client, world.User, Unique.Slug("globex"));

        string[] clientKeys =
        [
            "",
            " ",
            "../../x",
            "/abs/path",
            $"org/{otherOrg:D}/project/{world.Project:D}/run/x/attempt/{ExecutionKey}/1/{Guid.CreateVersion7():D}",
        ];

        foreach (var key in clientKeys)
        {
            ShouldBeRefused(await MintAsync(world, Command(clientKey: key)), 422, "object_key_not_allowed", $"client key [{key}]");
        }

        (await RowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ validator: execution key exact form

    [Fact]
    public async Task ExecutionKey_OutsideLowercaseSha256Hex_IsRefusedAtValidation_AndWritesNothing()
    {
        await using var world = await StartAsync();

        string[] executionKeys =
        [
            ExecutionKey[..63],
            ExecutionKey + "0",
            ExecutionKey.ToUpperInvariant(),
            ExecutionKey + "\n",
            "../" + ExecutionKey[..61],
        ];

        foreach (var key in executionKeys)
        {
            ShouldBeRefused(await MintAsync(world, Command(executionKey: key)), 422, null, $"execution key [{key}]");
        }

        (await RowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ kind: closed list, exact match

    [Fact]
    public async Task Kind_NotExactlyAContractKind_IsKindUnknown_AndWritesNothing()
    {
        await using var world = await StartAsync();

        string[] kinds = ["Trace", "trace ", "", "trace/../x", "screenshots"];

        foreach (var kind in kinds)
        {
            ShouldBeRefused(await MintAsync(world, Command(kind: kind)), 422, "kind_unknown", $"kind [{kind}]");
        }

        (await RowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ checksum: exact form

    [Fact]
    public async Task Checksum_OutsideTheExactForm_IsChecksumInvalid_AndWritesNothing()
    {
        await using var world = await StartAsync();

        string[] checksums =
        [
            Checksum[..^1],
            Checksum + "0",
            "SHA256:" + Checksum["sha256:".Length..],
            "sha256:" + new string('g', 64),
            Checksum + " ",
        ];

        foreach (var checksum in checksums)
        {
            ShouldBeRefused(await MintAsync(world, Command(checksum: checksum)), 422, "checksum_invalid", $"checksum [{checksum}]");
        }

        (await RowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ declared size: boundaries

    [Fact]
    public async Task DeclaredBytes_Boundaries_OneAndMaxAccepted_MinValueRefused()
    {
        await using var world = await StartAsync();

        var run = await CreateRunAsync(world, world.Org);
        (await MintAsync(world, Command(runId: run, bytes: 1))).IsSuccess.ShouldBeTrue("bytes = 1 is the smallest positive size");
        (await MintAsync(world, Command(runId: run, bytes: long.MaxValue))).IsSuccess.ShouldBeTrue("bytes = long.MaxValue fits the bigint column");
        ShouldBeRefused(await MintAsync(world, Command(bytes: long.MinValue)), 422, "size_out_of_range", "bytes = long.MinValue");

        (await RowsAsync(world)).ShouldBe(2);
    }

    // ------------------------------------------------------------------ media type: length bounds

    [Fact]
    public async Task MediaType_AsciiAndBmpBoundaries_AreAcceptedAt255_AndRefusedAbove()
    {
        await using var world = await StartAsync();

        ShouldBeRefused(await MintAsync(world, Command(mediaType: "")), 422, null, "empty media type");
        ShouldBeRefused(await MintAsync(world, Command(mediaType: new string('a', 256))), 422, null, "256 ASCII characters");
        var run = await CreateRunAsync(world, world.Org);
        (await MintAsync(world, Command(runId: run, mediaType: new string('a', 255)))).IsSuccess.ShouldBeTrue("255 ASCII characters");
        (await MintAsync(world, Command(runId: run, mediaType: new string('é', 255)))).IsSuccess.ShouldBeTrue("255 BMP characters");

        (await RowsAsync(world)).ShouldBe(2);
    }

    /// <summary>
    /// 0028 bounds <c>media_type</c> with <c>char_length between 1 and 255</c>, which counts code points; the
    /// validator counts runes too. 255 emoji are 255 code points (accepted) but 510 UTF-16 code units.
    /// </summary>
    [Fact]
    public async Task MediaType_255CodePoints_WithAstralCharacters_IsAccepted_BecauseTheCheckCountsCodePoints()
    {
        await using var world = await StartAsync();
        var astral255 = string.Concat(Enumerable.Repeat("😀", 255));
        var run = await CreateRunAsync(world, world.Org);

        (await MintAsync(world, Command(runId: run, mediaType: astral255))).IsSuccess.ShouldBeTrue(
            "255 code points is within the 0028 char_length CHECK; the validator counts code points, not UTF-16 units");
    }

    [Fact]
    public async Task MediaType_256CodePoints_WithAstralCharacters_IsRefused()
    {
        await using var world = await StartAsync();
        var astral256 = string.Concat(Enumerable.Repeat("😀", 256));

        ShouldBeRefused(await MintAsync(world, Command(mediaType: astral256)), 422, null, "256 code points");
    }

    [Fact]
    public async Task ClientKey_WithBadShape_StillReturnsObjectKeyNotAllowed_BeforeShapeValidation()
    {
        await using var world = await StartAsync();

        ShouldBeRefused(
            await MintAsync(world, Command(clientKey: "org/x", executionKey: "../x", attemptNo: 0, mediaType: string.Empty)),
            422,
            "object_key_not_allowed",
            "the client-key refusal comes first in the C2 order, ahead of the shape rules");
    }

    // ------------------------------------------------------------------ run id: 0031's composite key enforces existence

    [Fact]
    public async Task RunId_OfAnUnknownRun_IsRefusedAsNotFoundBeforeTheWrite()
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Command(runId: Guid.CreateVersion7()));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(EvidenceErrors.RunNotFound.Code);
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task RunId_OfASeededRun_IsAccepted()
    {
        await using var world = await StartAsync();

        (await MintAsync(world, Command(runId: await CreateRunAsync(world, world.Org)))).IsSuccess.ShouldBeTrue(
            "a run row of the same org satisfies the composite key");
    }

    // ------------------------------------------------------------------ sensitivity: static fallback per kind (T57(a))

    [Fact]
    public async Task Sensitivity_WithoutAPolicyRow_IsTheStaticFallbackForEachOfTheEightKinds()
    {
        await using var world = await StartAsync();

        var expected = new Dictionary<string, string>
        {
            [ArtifactKind.Trace] = ArtifactSensitivity.Sensitive,
            [ArtifactKind.DomSnapshot] = ArtifactSensitivity.Sensitive,
            [ArtifactKind.A11ySnapshot] = ArtifactSensitivity.Sensitive,
            [ArtifactKind.Patch] = ArtifactSensitivity.Sensitive,
            [ArtifactKind.Video] = ArtifactSensitivity.Normal,
            [ArtifactKind.Screenshot] = ArtifactSensitivity.Normal,
            [ArtifactKind.RawReport] = ArtifactSensitivity.Normal,
            [ArtifactKind.UiHierarchy] = ArtifactSensitivity.Normal,
        };

        ArtifactKind.All.Count.ShouldBe(8);
        foreach (var kind in ArtifactKind.All)
        {
            expected.ContainsKey(kind).ShouldBeTrue($"kind {kind} has no expected fallback");
        }

        var run = await CreateRunAsync(world, world.Org);
        foreach (var (kind, sensitivity) in expected)
        {
            var result = await MintAsync(world, Command(runId: run, kind: kind));
            result.IsSuccess.ShouldBeTrue(kind);

            (await ColumnAsync(world, result.Value.ArtifactId, "sensitivity")).ShouldBe(sensitivity, $"fallback for {kind}");
        }
    }

    [Fact]
    public async Task Sensitivity_ProjectPolicyRow_OverridesTheFallback_InBothDirections()
    {
        await using var world = await StartAsync();
        await InsertKindPolicyAsync(world.Database, world.Org, world.Project, ArtifactKind.Trace, ArtifactSensitivity.Normal);
        await InsertKindPolicyAsync(world.Database, world.Org, world.Project, ArtifactKind.Screenshot, ArtifactSensitivity.Sensitive);

        var run = await CreateRunAsync(world, world.Org);
        var trace = await MintAsync(world, Command(runId: run, kind: ArtifactKind.Trace));
        var screenshot = await MintAsync(world, Command(runId: run, kind: ArtifactKind.Screenshot));

        (await ColumnAsync(world, trace.Value.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Normal,
            "a sensitive-by-default kind lowered to normal by the project policy row");
        (await ColumnAsync(world, screenshot.Value.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive,
            "a normal-by-default kind raised to sensitive by the project policy row");
    }

    [Fact]
    public async Task Sensitivity_PolicyRowOfAnotherProjectOrAnotherOrg_DoesNotLeak()
    {
        await using var world = await StartAsync();
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("shop"));
        var otherOrg = await CreateOrgAsync(world.Client, world.User, Unique.Slug("globex"));
        var otherOrgProject = await CreateProjectAsync(world.Client, world.User, otherOrg, Unique.Slug("shop"));

        await InsertKindPolicyAsync(world.Database, world.Org, otherProject, ArtifactKind.Video, ArtifactSensitivity.Sensitive);
        await InsertKindPolicyAsync(world.Database, otherOrg, otherOrgProject, ArtifactKind.Screenshot, ArtifactSensitivity.Sensitive);

        var run = await CreateRunAsync(world, world.Org);
        var video = await MintAsync(world, Command(runId: run, kind: ArtifactKind.Video));
        var screenshot = await MintAsync(world, Command(runId: run, kind: ArtifactKind.Screenshot));

        (await ColumnAsync(world, video.Value.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Normal,
            "a policy row on a sibling project must not change this project's fallback");
        (await ColumnAsync(world, screenshot.Value.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Normal,
            "a policy row in another org must not change this org's fallback");
    }

    // ------------------------------------------------------------------ persisted row shape (0028 CHECKs, AC2)

    [Fact]
    public async Task MintedRow_PersistsTheServerChosenKey_AndTheContractColumns()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world, world.Org);

        var result = await MintAsync(world, Command(runId: run));
        result.IsSuccess.ShouldBeTrue();
        var record = result.Value;
        var id = record.ArtifactId;

        record.ObjectKey.ShouldBe($"org/{world.Org:D}/project/{world.Project:D}/run/{run:D}/attempt/{ExecutionKey}/1/{id:D}");
        record.State.ShouldBe("pending_upload");

        (await ColumnAsync(world, id, "object_key")).ShouldBe(record.ObjectKey);
        (await ColumnAsync(world, id, "org_id::text")).ShouldBe(world.Org.ToString("D"));
        (await ColumnAsync(world, id, "project_id::text")).ShouldBe(world.Project.ToString("D"));
        (await ColumnAsync(world, id, "run_id::text")).ShouldBe(run.ToString("D"));
        (await ColumnAsync(world, id, "execution_key")).ShouldBe(ExecutionKey);
        (await ColumnAsync(world, id, "attempt_no::text")).ShouldBe("1");
        (await ColumnAsync(world, id, "state")).ShouldBe("pending_upload");
        (await ColumnAsync(world, id, "scan_state")).ShouldBe("pending");
        (await ColumnAsync(world, id, "checksum")).ShouldBe(Checksum);
        (await ColumnAsync(world, id, "media_type")).ShouldBe(ZipType);
    }

    [Fact]
    public async Task TwoMints_ProduceDistinctIdsAndKeys()
    {
        await using var world = await StartAsync();

        var run = await CreateRunAsync(world, world.Org);
        var first = await MintAsync(world, Command(runId: run));
        var second = await MintAsync(world, Command(runId: run));

        first.Value.ArtifactId.ShouldNotBe(second.Value.ArtifactId);
        first.Value.ObjectKey.ShouldNotBe(second.Value.ObjectKey);
    }

    // ------------------------------------------------------------------ C2 pipeline: tenant and permission refusals

    [Fact]
    public async Task OrgWideSession_WithoutAProject_IsRefused403_AndWritesNothing()
    {
        await using var world = await StartAsync();

        ShouldBeRefused(await SendAsync(world, null, Writer(), Command()), 403, null, "org-wide session");

        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Principal_WithoutArtifactWrite_IsForbidden403_AndWritesNothing()
    {
        await using var world = await StartAsync();

        ShouldBeRefused(
            await SendAsync(world, world.Project, RequestPrincipal.Service(null, Array.Empty<string>()), Command()),
            403,
            "forbidden",
            "service principal without artifact:write");

        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Principal_ScopedToAnotherProject_IsRefused403_ProjectScopeMismatch_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("shop"));

        ShouldBeRefused(
            await SendAsync(world, world.Project, Writer(otherProject), Command()),
            403,
            "project_scope_mismatch",
            "service principal scoped to a sibling project");

        (await RowsAsync(world)).ShouldBe(0);
    }
}

/// <summary>
/// Pure unit probe of the minter's execution-key guard (no container): the minter anchors with <c>\A…\z</c>, so a
/// trailing newline is refused.
/// </summary>
public sealed class EvidenceArtifactMinterQaTests
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Mint_RefusesAnExecutionKeyWithATrailingNewline() =>
        Should.Throw<ArgumentException>(() =>
            ArtifactKeyMinter.Mint(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ExecutionKey + "\n", 1, Guid.CreateVersion7()));
}
