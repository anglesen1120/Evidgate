using System.Data.Common;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The list and detail reads: keyset pages over active rows, the closed kind filter, bounded page sizes and
/// one read-only statement per page.</summary>
public sealed partial class TemplateTests
{
    [Fact]
    public async Task List_WalksTheKeysetPages()
    {
        await using var world = await StartAsync();
        var first = await SeedAsync(world, createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var second = await SeedAsync(world, createdAt: new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));
        var third = await SeedAsync(world, createdAt: new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero));

        var pageOne = await ListAsync(world, "?limit=2");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, pageOne.ErrorMessage);
        Ids(pageOne).ShouldBe([first, second], "the page is the oldest two rows in (created_at, id) order");
        var cursor = pageOne.Body.GetProperty("next_cursor");
        cursor.ValueKind.ShouldBe(JsonValueKind.String, "a page that is not last carries a cursor");

        var pageTwo = await ListAsync(world, $"?limit=2&cursor={Uri.EscapeDataString(cursor.GetString()!)}");
        pageTwo.Status.ShouldBe(HttpStatusCode.OK, pageTwo.ErrorMessage);
        Ids(pageTwo).ShouldBe([third]);
        pageTwo.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null, "the last page carries no cursor");
    }

    [Fact]
    public async Task List_FiltersByKind_AndIgnoresABlankKind()
    {
        await using var world = await StartAsync();
        var web = await SeedAsync(world, kind: "web", createdAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var api = await SeedAsync(world, kind: "api", createdAt: new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
        var mobile = await SeedAsync(world, kind: "mobile", createdAt: new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero));

        var filtered = await ListAsync(world, "?kind=web");
        filtered.Status.ShouldBe(HttpStatusCode.OK, filtered.ErrorMessage);
        Ids(filtered).ShouldBe([web]);

        var all = await ListAsync(world, "?kind=");
        all.Status.ShouldBe(HttpStatusCode.OK, all.ErrorMessage);
        Ids(all).ShouldBe([web, api, mobile], "a blank kind is every kind, in the keyset order");
    }

    [Fact]
    public async Task List_RejectsAnUnknownKindAMalformedCursorAndAnOutOfRangeLimit()
    {
        await using var world = await StartAsync();

        AssertInvalid(await ListAsync(world, "?kind=desktop"), "kind", "invalid");
        AssertInvalid(await ListAsync(world, "?cursor=not-a-cursor"), "cursor", "malformed");
        AssertInvalid(await ListAsync(world, "?limit=0"), "limit", "out_of_range");
        AssertInvalid(await ListAsync(world, "?limit=201"), "limit", "out_of_range");
        AssertInvalid(await ListAsync(world, "?limit=abc"), "limit", "out_of_range");

        var largest = await ListAsync(world, "?limit=200");
        largest.Status.ShouldBe(HttpStatusCode.OK, largest.ErrorMessage);
    }

    [Fact]
    public async Task List_RunsOneStatementOverTheTemplateTable()
    {
        await using var world = await StartAsync();
        await SeedAsync(world);

        world.Api.Statements.Clear();
        (await ListAsync(world)).Status.ShouldBe(HttpStatusCode.OK);

        var statements = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("from public.template", StringComparison.Ordinal))
            .ToList();
        statements.Count.ShouldBe(1, "one page is one statement; the limit+1 row decides has-more without a count");
    }

    [Fact]
    public async Task ListTemplates_RunsInAReadOnlyTransaction()
    {
        var holder = new ProbeHolder();
        await using var world = await StartAsync(services =>
        {
            services.RemoveAll<ITenantDb>();
            services.AddSingleton<TenantDb>();
            services.AddSingleton<ITenantDb>(provider =>
                holder.Value = new ReadOnlyProbeTenantDb(provider.GetRequiredService<TenantDb>()));
        });
        await SeedAsync(world);

        (await ListAsync(world)).Status.ShouldBe(HttpStatusCode.OK, "the probe host serves the same page");

        holder.Value.ShouldNotBeNull("the host resolved the probe over the tenant db");
        holder.Value.Reads.ShouldBe([true], "the list statement ran in a read-only transaction");
        holder.Value.Writes.ShouldBe(0, "a read route never executes a write");
    }

    [Fact]
    public async Task Get_ReturnsTheStoredRulesAndBodyReference()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world, rules: """{"rules":[{"name":"lint","command":"npm run lint"}]}""");

        var answer = await GetAsync(world, id);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("body_ref").GetString().ShouldBe(BodyRef);
        answer.Body.GetProperty("conformance_rules").GetProperty("rules")[0].GetProperty("name").GetString().ShouldBe("lint");
    }

    [Fact]
    public async Task List_AnswersASummaryWithoutTheRules()
    {
        await using var world = await StartAsync();
        await SeedAsync(world);

        var answer = await ListAsync(world);
        var item = answer.Body.GetProperty("items")[0];
        item.TryGetProperty("conformance_rules", out _).ShouldBeFalse("the page is a summary; the rules belong to the detail read");
        item.GetProperty("body_ref").GetString().ShouldBe(BodyRef);
    }

    private sealed class ProbeHolder
    {
        public ReadOnlyProbeTenantDb? Value { get; set; }
    }

    /// <summary>Delegates to the real tenant db and records whether each Dapper read ran in a read-only transaction.</summary>
    private sealed class ReadOnlyProbeTenantDb(ITenantDb inner) : ITenantDb
    {
        public List<bool> Reads { get; } = [];

        public int Writes { get; private set; }

        public DbConnection Connection => inner.Connection;

        public DbTransaction Transaction => inner.Transaction;

        public async Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Reads.Add(await inner.ScalarAsync<bool>(
                "select current_setting('transaction_read_only') = 'on'",
                cancellationToken: cancellationToken));
            return await inner.QueryAsync<T>(sql, parameters, cancellationToken);
        }

        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);

        public Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Writes++;
            return inner.ExecuteAsync(sql, parameters, cancellationToken);
        }

        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.ScalarAsync<T>(sql, parameters, cancellationToken);
    }
}
