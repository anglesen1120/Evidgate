using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.7.1-T2.2/T2.3 through the real C2 pipeline and a real PostgreSQL: the
/// <c>adapter_capability</c> migration (FORCE RLS, revoked UPDATE/DELETE), the
/// registration command's schema/negotiation/upsert path, the first-send seam, and
/// the tenant boundary. Every registration runs through the host's Mediator with the
/// tenant and a service principal holding <c>event:write</c> published for the flow —
/// the same stages the future ingest entry point (04.1.1-T1) will drive.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AdapterCapabilityTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AdapterCapabilityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    // ------------------------------------------------------------------ documents

    /// <summary>
    /// A capability.v1 document in the shape of <c>contracts/examples/capability.valid.json</c>.
    /// </summary>
    private static string Document(
        int major = 1,
        int minor = 0,
        string mode = "full",
        bool engine = true,
        IReadOnlyDictionary<string, bool>? extraSupports = null,
        bool extraRootMember = false)
    {
        var supports = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["discovery"] = true,
            ["step_events"] = true,
            ["retry"] = true,
            ["cancel"] = true,
        };

        if (extraSupports is not null)
        {
            foreach (var (flag, value) in extraSupports)
            {
                supports[flag] = value;
            }
        }

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["protocol"] = new { major, minor },
            ["platforms"] = new[] { "linux-x64", "linux-arm64" },
            ["supports"] = supports,
            ["mode"] = mode,
        };

        if (engine)
        {
            document["engine"] = new { family = "playwright", version = "1.49.0" };
        }

        if (extraRootMember)
        {
            document["extra"] = 1;
        }

        return JsonSerializer.Serialize(document);
    }

    /// <summary>The first envelope of a run carrying a capability document (00 §9.4; the envelope member is <c>type</c>).</summary>
    private static string Carrier(string schema, string capability) =>
        "{\"type\":\"run.log\",\"sequence\":1,\"ext\":{\"schema\":\"" + schema + "\",\"capability\":" + capability + "}}";

    // ------------------------------------------------------------------ pipeline drivers

    /// <summary>
    /// One registration through the real Mediator pipeline (telemetry → validation →
    /// tenant transaction → authorization → idempotency → handler → audit → commit):
    /// the tenant and a service principal holding <c>event:write</c> are published for
    /// the flow, exactly as the ingest entry point will.
    /// </summary>
    private static async Task<Result<AdapterCapabilityRegistered>> RegisterAsync(World world, string name, string document)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(null, [Permissions.Event.Write]));

        return await provider.GetRequiredService<IMediator>()
            .Send(new RegisterAdapterCapability(name, Encoding.UTF8.GetBytes(document)), TestContext.Current.CancellationToken);
    }

    /// <summary>One first-send registration through the same real pipeline.</summary>
    private static async Task<Result<FirstSendCapabilityOutcome>> FirstSendAsync(World world, string envelope)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(null, [Permissions.Event.Write]));

        return await provider.GetRequiredService<IMediator>()
            .Send(new RegisterFirstSendCapability(Encoding.UTF8.GetBytes(envelope)), TestContext.Current.CancellationToken);
    }

    private async Task<long> RowsAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability");

    // ------------------------------------------------------------------ registration

    [Fact]
    public async Task RegisterCapability_FullMode_UpsertsRow()
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(world, "playwright", Document());

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Created.ShouldBeTrue();
        result.Value.Id.ShouldNotBe(Guid.Empty);

        (await RowsAsync(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, "select name from public.adapter_capability")).ShouldBe("playwright");
        (await _fixture.ScalarAsync<string>(world.Database, "select mode from public.adapter_capability")).ShouldBe("full");
        (await _fixture.ScalarAsync<int>(world.Database, "select protocol_major from public.adapter_capability")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(world.Database, "select protocol_minor from public.adapter_capability")).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(world.Database, "select engine_family from public.adapter_capability")).ShouldBe("playwright");
        (await _fixture.ScalarAsync<string>(world.Database, "select engine_version from public.adapter_capability")).ShouldBe("1.49.0");
    }

    [Fact]
    public async Task RegisterCapability_MajorMismatch_422()
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(world, "playwright", Document(major: 2));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.UnsupportedProtocolMajor);
        result.Error.Status.ShouldBe(422);
        result.Error.Retryable.ShouldBeFalse();
        result.Error.Details!.ShouldContain(detail => detail.Reason == "adapter_major=2");
        result.Error.Details!.ShouldContain(detail => detail.Reason == "supported_majors=1");
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterCapability_UnknownSupports_Preserved()
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(
            world,
            "playwright",
            Document(extraSupports: new Dictionary<string, bool>(StringComparer.Ordinal) { ["strict_mode"] = true }));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        // The unknown flag is stored verbatim in the supports jsonb and never evaluated (AC3).
        var supports = await _fixture.ScalarAsync<string>(world.Database, "select supports::text from public.adapter_capability");
        supports.ShouldContain("strict_mode");
        (await _fixture.ScalarAsync<bool>(world.Database, "select (supports ->> 'strict_mode')::boolean from public.adapter_capability"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task RegisterCapability_MissingEngine_422()
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(world, "playwright", Document(engine: false));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.CapabilityInvalid);
        result.Error.Status.ShouldBe(422);
        result.Error.Details!.ShouldContain(detail => detail.Path == "/engine");
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterCapability_UndefinedRootMember_422()
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(world, "playwright", Document(extraRootMember: true));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.CapabilityInvalid);
        // The closed root names the offending member (AC3).
        result.Error.Details!.ShouldContain(detail => detail.Path == "/extra");
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterCapability_OversizeDocument_422()
    {
        await using var world = await StartAsync();

        // > the capability.v1 x-max-bytes cap (16384); trailing whitespace is valid JSON,
        // so only the transport cap can refuse it.
        var oversize = Document() + new string(' ', 17_000);

        var result = await RegisterAsync(world, "playwright", oversize);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.CapabilityInvalid);
        result.Error.Details!.ShouldContain(detail => detail.Reason == "body_too_large");
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Theory]
    // Uppercase is outside the ^[a-z0-9][a-z0-9._-]{0,63}$ grammar the column CHECK enforces.
    [InlineData("Playwright")]
    // The ext.schema form is not a bare name.
    [InlineData("playwright/1")]
    // An empty name.
    [InlineData("")]
    // A space is outside the grammar.
    [InlineData("play wright")]
    // A leading separator is outside the grammar.
    [InlineData("-playwright")]
    // 65 characters: one past the 64-character cap.
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    // A trailing newline: .NET's $ would accept it, the column CHECK refuses it.
    [InlineData("playwright\n")]
    public async Task RegisterCapability_InvalidName_422_ZeroRows(string name)
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(world, name, Document());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.CapabilityInvalid);
        result.Error.Status.ShouldBe(422);
        result.Error.Retryable.ShouldBeFalse();
        result.Error.Details!.ShouldContain(detail => detail.Path == "/name");
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterCapability_IngestOnly_RegistersAndStoresMode()
    {
        await using var world = await StartAsync();

        var result = await RegisterAsync(world, "playwright", Document(mode: "ingest_only"));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        // The stored row carries the mode the document declared — the input the gate-eligibility rule
        // reads (AC4). The rule itself is the pure AdapterModeRules function, covered by
        // ProtocolNegotiatorUnitTests.AdapterModeRules_IsGateEligible_TruthTable.
        (await _fixture.ScalarAsync<string>(world.Database, "select mode from public.adapter_capability")).ShouldBe("ingest_only");
    }

    [Fact]
    public async Task RegisterCapability_RepeatedIdenticalDocument_SameIdOneRow()
    {
        await using var world = await StartAsync();

        var first = await RegisterAsync(world, "playwright", Document());
        var again = await RegisterAsync(world, "playwright", Document());

        first.Value.Created.ShouldBeTrue();
        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        again.Value.Created.ShouldBeFalse();
        again.Value.Id.ShouldBe(first.Value.Id);
        (await RowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task RegisterCapability_SameNaturalKeyDifferentMode_409()
    {
        await using var world = await StartAsync();

        var first = await RegisterAsync(world, "playwright", Document(mode: "full"));
        var conflict = await RegisterAsync(world, "playwright", Document(mode: "ingest_only"));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        conflict.IsSuccess.ShouldBeFalse();
        conflict.Error!.Code.ShouldBe(ApiErrorCode.CapabilityConflict);
        conflict.Error.Status.ShouldBe(409);
        // Capability rows are first-write facts: the stored row wins and is unchanged.
        (await RowsAsync(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, "select mode from public.adapter_capability")).ShouldBe("full");
    }

    [Fact]
    public async Task RegisterCapability_StatementBudget()
    {
        await using var world = await StartAsync();

        world.Api.Statements.Clear();
        var first = await RegisterAsync(world, "playwright", Document());
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);

        var inserts = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("insert into public.adapter_capability", StringComparison.Ordinal))
            .ToList();
        inserts.Count.ShouldBe(1, string.Join(Environment.NewLine, world.Api.Statements.Snapshot()));

        world.Api.Statements.Clear();
        var again = await RegisterAsync(world, "playwright", Document());
        again.IsSuccess.ShouldBeTrue(again.Error?.Message);

        var repeat = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("adapter_capability", StringComparison.Ordinal))
            .ToList();
        // One skipped insert plus at most one re-read; nothing is written on a repeat.
        repeat.Count.ShouldBeLessThanOrEqualTo(2, string.Join(Environment.NewLine, repeat));
    }

    // ------------------------------------------------------------------ the table and the tenant boundary

    [Fact]
    public async Task AdapterCapability_RlsEnabledAndForced()
    {
        await using var world = await StartAsync();

        (await _fixture.TableExistsAsync(world.Database, "adapter_capability")).ShouldBeTrue();
        var rls = await _fixture.RowSecurityAsync(world.Database, "adapter_capability");
        rls.Enabled.ShouldBeTrue();
        rls.Forced.ShouldBeTrue();
    }

    [Fact]
    public async Task AdapterCapability_AppRoleUpdateDelete_Denied()
    {
        await using var world = await StartAsync();
        (await RegisterAsync(world, "playwright", Document())).IsSuccess.ShouldBeTrue();

        foreach (var statement in new[]
        {
            "update public.adapter_capability set mode = 'full'",
            "delete from public.adapter_capability",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

            (await scope.CaptureErrorAsync(statement)).SqlState.ShouldBe("42501", statement);
        }
    }

    [Fact]
    public async Task AdapterCapability_NoTenantContext_ZeroRows()
    {
        await using var world = await StartAsync();
        (await RegisterAsync(world, "playwright", Document())).IsSuccess.ShouldBeTrue();
        (await RowsAsync(world)).ShouldBe(1);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.adapter_capability")).ShouldBe(0);
    }

    [Fact]
    public async Task AdapterCapability_CrossTenant_ZeroRows()
    {
        await using var world = await StartAsync();
        (await RegisterAsync(world, "playwright", Document())).IsSuccess.ShouldBeTrue();

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            Guid.CreateVersion7());

        (await scope.ScalarAsync<long>("select count(*) from public.adapter_capability")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ first send (T2.3)

    [Fact]
    public async Task FirstSend_ValidCarrier_RegistersRowNamedFromSchema()
    {
        await using var world = await StartAsync();

        var result = await FirstSendAsync(world, Carrier("playwright/1", Document()));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Registered.ShouldBeTrue();
        result.Value.CapabilityId.ShouldNotBeNull();
        (await _fixture.ScalarAsync<string>(world.Database, "select name from public.adapter_capability")).ShouldBe("playwright");
        (await RowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task FirstSend_BadMajor_422_ZeroRows()
    {
        await using var world = await StartAsync();

        var result = await FirstSendAsync(world, Carrier("playwright/2", Document(major: 2)));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.UnsupportedProtocolMajor);
        result.Error.Retryable.ShouldBeFalse();
        result.Error.Details!.ShouldContain(detail => detail.Reason == "adapter_major=2");
        result.Error.Details!.ShouldContain(detail => detail.Reason == "supported_majors=1");
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task FirstSend_NonCarrier_NotRegistered_ZeroRows()
    {
        await using var world = await StartAsync();

        var result = await FirstSendAsync(
            world,
            """{"type":"run.step","sequence":1,"ext":{"schema":"playwright/1"}}""");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Registered.ShouldBeFalse();
        result.Value.CapabilityId.ShouldBeNull();
        (await RowsAsync(world)).ShouldBe(0);
    }
}
