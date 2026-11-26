using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The extraction job against a real database: state progression, crash recovery, the per-tenant slot (taken,
/// held, released on every exit) and the refusals that come from the caps persisted on the row. The object fetch is a
/// delegate, so no store is involved and the cases can observe that it happens outside any transaction.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class AuthoringIntakeJobTests(PostgresTenancyFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StateProgression()
    {
        var world = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx("A requirement");
        var intake = await SeedDocumentAsync(world, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var fetches = 0;
        var job = CreateJob(api.Services, async (_, limit, _) =>
        {
            (await StateAsync(world, intake)).ShouldBe("extracting");
            (await SlotCountAsync(world, intake)).ShouldBe(1);
            limit.ShouldBe(IntakeCaps.Default.MaxDocumentBytes);
            fetches++;
            return bytes;
        });

        (await StateAsync(world, intake)).ShouldBe("received");
        await job.RunAsync(intake, world.Org, Token);

        fetches.ShouldBe(1);
        (await StateAsync(world, intake)).ShouldBe("ready");
        var text = (await ColumnAsync<string>(world, intake, "extracted_text")).ShouldNotBeNull();
        text.ShouldContain("A requirement");
        (await ColumnAsync<string>(world, intake, "extracted_text_hash")).ShouldBe(IntakeText.Hash(text));
        (await ColumnAsync<string>(world, intake, "failure_code")).ShouldBeNull();
        (await SlotCountAsync(world, intake)).ShouldBe(0);

        // A redelivery of the same task finds the row terminal: no fetch, no write, version unchanged.
        var version = await ColumnAsync<int>(world, intake, "version");
        await job.RunAsync(intake, world.Org, Token);
        fetches.ShouldBe(1);
        (await ColumnAsync<int>(world, intake, "version")).ShouldBe(version);
    }

    [Theory]
    [InlineData("e\u0301")]
    [InlineData("\u00e9")]
    public async Task Extract_CanonicallyEquivalentDocuments_StoreComposedTextAndHash(string input)
    {
        const string composed = "\u00e9";
        var world = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx(input);
        var intake = await SeedDocumentAsync(world, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var job = CreateJob(api.Services, (_, _, _) => Task.FromResult<byte[]?>(bytes));

        await job.RunAsync(intake, world.Org, Token);

        (await ColumnAsync<string>(world, intake, "extracted_text")).ShouldBe(composed);
        (await ColumnAsync<string>(world, intake, "extracted_text_hash")).ShouldBe(IntakeText.Hash(composed));
    }

    [Fact]
    public async Task RejectedIsReadableOnTheRow()
    {
        var world = await SeedWorldAsync();
        var bytes = "this is not a zip container"u8.ToArray();
        var intake = await SeedDocumentAsync(world, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var job = CreateJob(api.Services, (_, _, _) => Task.FromResult<byte[]?>(bytes));

        await job.RunAsync(intake, world.Org, Token);

        (await StateAsync(world, intake)).ShouldBe("rejected");
        (await ColumnAsync<string>(world, intake, "failure_code")).ShouldBe(IntakeFailureCode.ContainerMismatch);
        (await ColumnAsync<string>(world, intake, "extracted_text")).ShouldBeNull();
        (await SlotCountAsync(world, intake)).ShouldBe(0);

        var version = await ColumnAsync<int>(world, intake, "version");
        await job.RunAsync(intake, world.Org, Token);
        (await ColumnAsync<int>(world, intake, "version")).ShouldBe(version);
    }

    [Fact]
    public async Task CrashRecovery_Idempotent()
    {
        var world = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx("Recovered");
        var intake = await SeedDocumentAsync(world, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var store = ActivatorUtilities.CreateInstance<IntakeExtractionStore>(api.Services);

        // An attempt claims the row and dies: the row is extracting and its slot lease is live.
        var crashed = (await store.ClaimAsync(world.Org, intake, Token)).ShouldNotBeNull();
        (await StateAsync(world, intake)).ShouldBe("extracting");
        var fetches = 0;
        var job = CreateJob(api.Services, (_, _, _) =>
        {
            fetches++;
            return Task.FromResult<byte[]?>(bytes);
        });

        // While the dead attempt's lease is live, the same intake waits: it is deferred, not run twice.
        await Should.ThrowAsync<TaskDeferredException>(() => job.RunAsync(intake, world.Org, Token));
        fetches.ShouldBe(0);

        await fixture.ExecuteAsync(
            world.Database,
            $"update public.authoring_intake_slot set expires_at = now() - interval '1 second' where intake_id = '{intake:D}'");
        await job.RunAsync(intake, world.Org, Token);

        (await StateAsync(world, intake)).ShouldBe("ready");
        fetches.ShouldBe(1);

        // The dead attempt waking up late holds no lease any more and cannot rewrite the terminal row.
        (await store.FinishAsync(world.Org, crashed, ExtractionOutcome.Refused(IntakeFailureCode.ExtractionFailed), true, Token))
            .ShouldBeFalse();
        (await StateAsync(world, intake)).ShouldBe("ready");
        (await ColumnAsync<string>(world, intake, "failure_code")).ShouldBeNull();
    }

    [Fact]
    public async Task ThirdExtractionWaitsUntilASlotIsReleased()
    {
        var world = await SeedWorldAsync();
        var other = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx("Work");
        var first = await SeedDocumentAsync(world, bytes);
        var second = await SeedDocumentAsync(world, bytes);
        var third = await SeedDocumentAsync(world, bytes);
        var independent = await SeedDocumentAsync(other, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var store = ActivatorUtilities.CreateInstance<IntakeExtractionStore>(api.Services);
        (await store.ClaimAsync(world.Org, first, Token)).ShouldNotBeNull();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = CreateJob(api.Services, async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }).RunAsync(second, world.Org, shutdown.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        (await SlotCountAsync(world)).ShouldBe(2);

        // Two of two slots are held by live leases: the third is deferred and stays received; another tenant is unaffected.
        await Should.ThrowAsync<TaskDeferredException>(() => store.ClaimAsync(world.Org, third, Token));
        (await StateAsync(world, third)).ShouldBe("received");
        await CreateJob(api.Services, (_, _, _) => Task.FromResult<byte[]?>(bytes)).RunAsync(independent, other.Org, Token);
        (await StateAsync(other, independent)).ShouldBe("ready");

        // Cancelling the running attempt frees its slot at once, long before the lease would expire.
        await shutdown.CancelAsync();
        Exception? cancelled = null;
        try
        {
            await stalled;
        }
        catch (OperationCanceledException exception)
        {
            cancelled = exception;
        }

        cancelled.ShouldNotBeNull();
        (await SlotCountAsync(world)).ShouldBe(1);
        (await StateAsync(world, second)).ShouldBe("extracting");

        (await store.ClaimAsync(world.Org, third, Token)).ShouldNotBeNull();
        (await SlotCountAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task SlotIsReleasedWhenTheFetchFails_AndTheRowRecovers()
    {
        var world = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx("Retry");
        var intake = await SeedDocumentAsync(world, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var failing = CreateJob(api.Services, (_, _, _) => throw new IOException("store unavailable"));

        await Should.ThrowAsync<IOException>(() => failing.RunAsync(intake, world.Org, Token));

        (await SlotCountAsync(world, intake)).ShouldBe(0);
        (await StateAsync(world, intake)).ShouldBe("extracting");

        await CreateJob(api.Services, (_, _, _) => Task.FromResult<byte[]?>(bytes)).RunAsync(intake, world.Org, Token);
        (await StateAsync(world, intake)).ShouldBe("ready");
    }

    [Fact]
    public async Task ExtractionDeadlineRejectsWithExtractionTimeout()
    {
        var world = await SeedWorldAsync();
        var intake = await SeedDocumentAsync(world, AuthoringExtractionFixtures.Docx("Slow"));
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = CreateJob(
            api.Services,
            async (_, _, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            },
            clock).RunAsync(intake, world.Org, Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        clock.Advance(IntakeCaps.Default.ExtractionTimeout + TimeSpan.FromSeconds(1));
        await run;

        (await StateAsync(world, intake)).ShouldBe("rejected");
        (await ColumnAsync<string>(world, intake, "failure_code")).ShouldBe(IntakeFailureCode.ExtractionTimeout);
        (await SlotCountAsync(world, intake)).ShouldBe(0);
    }

    [Theory]
    [InlineData("size", IntakeFailureCode.ArchiveLimitExceeded)]
    [InlineData("unreadable", IntakeFailureCode.ExtractionFailed)]
    [InlineData("policy_off", IntakeFailureCode.ExtractionFailed)]
    [InlineData("media_mismatch", IntakeFailureCode.ContainerMismatch)]
    [InlineData("length_mismatch", IntakeFailureCode.ExtractionFailed)]
    [InlineData("missing_object", IntakeFailureCode.ExtractionFailed)]
    public async Task RefusalsComeFromTheRowAndThePolicyWithoutAnExtraction(string scenario, string expected)
    {
        var world = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx("Refused");
        var intake = await SeedScenarioAsync(world, bytes, scenario);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        byte[]? served = scenario switch
        {
            "length_mismatch" => [.. bytes, 0],
            "missing_object" => null,
            _ => bytes,
        };
        var fetches = 0;
        var job = CreateJob(api.Services, (_, _, _) =>
        {
            fetches++;
            return Task.FromResult<byte[]?>(served);
        });

        await job.RunAsync(intake, world.Org, Token);

        (await StateAsync(world, intake)).ShouldBe("rejected");
        (await ColumnAsync<string>(world, intake, "failure_code")).ShouldBe(expected);
        (await ColumnAsync<string>(world, intake, "extracted_text")).ShouldBeNull();
        (await SlotCountAsync(world, intake)).ShouldBe(0);

        // Only the cases that must read the bytes read them: the row's own cap and an unreadable cap never fetch.
        fetches.ShouldBe(scenario is "length_mismatch" or "missing_object" ? 1 : 0);
    }

    private static ExtractIntakeJob CreateJob(
        IServiceProvider services,
        Func<IntakeArtifact, long, CancellationToken, Task<byte[]?>> fetch,
        TimeProvider? time = null,
        IParsing? parsing = null)
    {
        var real = services.GetRequiredService<IIntakeArtifactReader>();
        var scopes = services.GetRequiredService<ITenantScopeAccessor>();
        var store = ActivatorUtilities.CreateInstance<IntakeExtractionStore>(services);
        var observed = new ObservedReader(real, scopes, fetch);

        return new ExtractIntakeJob(
            store,
            observed,
            new DocumentTextExtractor(parsing ?? services.GetRequiredService<IParsing>()),
            time ?? TimeProvider.System);
    }

    /// <summary>The real metadata read, with a fetch that also asserts no transaction is open around it.</summary>
    private sealed class ObservedReader(
        IIntakeArtifactReader inner,
        ITenantScopeAccessor scopes,
        Func<IntakeArtifact, long, CancellationToken, Task<byte[]?>> fetch) : IIntakeArtifactReader
    {
        public Task<IntakeArtifact?> ReadEligibleAsync(
            Guid orgId,
            Guid projectId,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            inner.ReadEligibleAsync(orgId, projectId, artifactId, cancellationToken);

        public Task<byte[]?> FetchAsync(IntakeArtifact artifact, long maxBytes, CancellationToken cancellationToken)
        {
            scopes.Current.ShouldBeNull();

            return fetch(artifact, maxBytes, cancellationToken);
        }
    }
}
