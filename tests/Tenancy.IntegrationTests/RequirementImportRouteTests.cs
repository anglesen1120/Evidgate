using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AuthoringImportCalls;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Requirement import at the HTTP edge of the import route: CSV, XLSX and Markdown files of one outline each create the
/// same three requirements in one audited batch, a re-import writes nothing and no audit row, and Markdown stays an
/// import-only format.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RequirementImportRouteTests : IClassFixture<TenancyHostPool>
{
    private const string MarkdownOutline =
        "# Checkout\nAs a shopper I pay for my cart.\n\n- Card is charged\n- Receipt is sent\n";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    /// <summary>Wires the fixtures.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    /// <param name="hosts">The pooled hosts.</param>
    public RequirementImportRouteTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        internal Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task Import_OutlineInAnyFormat_CreatesThreeRequirementsInOneAuditedBatch(string format)
    {
        await using var world = await StartAsync();
        var auditsBefore = await AuditsAsync(world);

        var answer = await ImportAsync(world.Client, world.As, world.Project, format, await BodyOf(format));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.Text);
        answer.BatchId.ShouldNotBeNull();
        answer.Count("created").ShouldBe(3);
        answer.Outcomes.ShouldAllBe(outcome => outcome == "create");
        answer.Rows.Select(row => row.GetProperty("kind").GetString()).ShouldAllBe(kind => kind == "requirement");
        (await AuditsAsync(world)).ShouldBe(auditsBefore + 1);
        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select kind from public.requirement where org_id = '{world.Org:D}' order by kind, title"))
            .ShouldBe(["acceptance_criterion", "acceptance_criterion", "user_story"]);

        var again = await ImportAsync(world.Client, world.As, world.Project, format, await BodyOf(format));

        again.Status.ShouldBe(HttpStatusCode.OK, again.Text);
        again.BatchId.ShouldBeNull();
        again.Count("unchanged").ShouldBe(3);
        (await AuditsAsync(world)).ShouldBe(auditsBefore + 1, "an unchanged re-import is not a mutating command");
    }

    [Fact]
    public async Task Import_MarkdownDryRun_WritesNothing()
    {
        await using var world = await StartAsync();

        var answer = await ImportAsync(world.Client, world.As, world.Project, "markdown", MarkdownOutline, dryRun: true);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.Text);
        answer.Count("created").ShouldBe(3);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.requirement where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_MarkdownWithOrphanList_IsRefusedWithTheSourceLine()
    {
        await using var world = await StartAsync();

        var answer = await ImportAsync(world.Client, world.As, world.Project, "markdown", "\n\n- orphan\n# Story\n- ok\n");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.Text);
        answer.Details.ShouldContain(detail => detail.Reason == "orphan_row" && detail.Index == 3);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.requirement where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_ConcurrentSameFileWithDifferentKeys_CreatesOneRequirementPerReference()
    {
        await using var world = await StartAsync();
        using var csv = new MemoryStream();
        await CsvFormat.WriteAsync(
            [
                RequirementSample.Row(1, "s1", null, "user_story", "Checkout", code: "REQ-1"),
                RequirementSample.Row(2, "s2", null, "user_story", "Shipping", code: "REQ-2"),
            ],
            csv,
            TestContext.Current.CancellationToken);
        var body = csv.ToArray();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var guard = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
        guard.Parameters.AddWithValue("key", $"requirement_import:{world.Org:D}:{world.Project:D}");
        await guard.ExecuteNonQueryAsync(cancellationToken);
        await using var pid = new NpgsqlCommand("select pg_backend_pid()", connection, transaction);
        var blockerPid = (int)(await pid.ExecuteScalarAsync(cancellationToken))!;
        Task<ImportAnswer> first;
        Task<ImportAnswer> second;
        using (ExecutionContext.SuppressFlow())
        {
            first = Task.Run(() => ImportAsync(
                world.Client, world.As, world.Project, "csv", body, idempotencyKey: Guid.CreateVersion7().ToString("N")));
            second = Task.Run(() => ImportAsync(
                world.Client, world.As, world.Project, "csv", body, idempotencyKey: Guid.CreateVersion7().ToString("N")));
        }

        await WaitForImportWaitersAsync(world, blockerPid, first, second);
        await transaction.CommitAsync(cancellationToken);
        var answers = await Task.WhenAll(first, second);

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        answers.Sum(answer => answer.Count("created")).ShouldBe(2);
        answers.Sum(answer => answer.Count("unchanged")).ShouldBe(2);
        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select external_ref || '|' || count(*) from public.requirement where org_id = '{world.Org:D}' "
                + "group by external_ref order by external_ref")).ShouldBe(["REQ-1|1", "REQ-2|1"]);
    }

    private async Task WaitForImportWaitersAsync(World world, int blockerPid, Task first, Task second)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(4))
        {
            first.IsCompleted.ShouldBeFalse("the first import must wait for the project import lock");
            second.IsCompleted.ShouldBeFalse("the second import must wait for the project import lock");
            var waiting = await _fixture.ScalarAsync<int>(
                world.Database,
                $"select count(*)::int from pg_stat_activity where {blockerPid} = any(pg_blocking_pids(pid))");
            if (waiting >= 2)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("both imports must overlap while waiting for the project import lock");
    }

    [Fact]
    public async Task Export_Markdown_IsRefusedAsUnsupportedFormat()
    {
        await using var world = await StartAsync();

        var answer = await ExportAsync(world.Client, world.As, world.Project, "markdown");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorDetails.ShouldContain(("/format", "unsupported_format"));
    }

    private static async Task<byte[]> BodyOf(string format)
    {
        if (format == "markdown")
        {
            return Encoding.UTF8.GetBytes(MarkdownOutline);
        }

        using var stream = new MemoryStream();
        var rows = RequirementSample.Outline();
        if (format == "csv")
        {
            await CsvFormat.WriteAsync(rows, stream);
        }
        else
        {
            await XlsxFormat.WriteAsync(rows, stream, TestContext.Current.CancellationToken);
        }

        return stream.ToArray();
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    private Task<long> AuditsAsync(World world) => _fixture.ScalarAsync<long>(
        world.Database,
        $"select count(*) from public.audit_log where org_id = '{world.Org:D}'");
}
