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
/// one read-only statement per page or detail.</summary>
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
    public async Task List_TiedCreatedAt_WalksTheTieByTemplateIdWithoutGapsOrDuplicates()
    {
        await using var world = await StartAsync();
        var tied = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 3; index++)
        {
            await SeedAsync(world, createdAt: tied);
        }

        // An identical created_at on every row leaves only the id tie-break to order a page of one.
        (await _fixture.ScalarAsync<long>(
                world.Database,
                $"select count(distinct created_at) from public.template where org_id = '{world.Org}'"))
            .ShouldBe(1, "every seeded row shares one instant");

        var expected = await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
             select id::text from public.template
              where org_id = '{world.Org}'
              order by created_at, id
             """);
        expected.Count.ShouldBe(3);

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var url = "?limit=1" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var answer = await ListAsync(world, url);

            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            var ids = Ids(answer);
            ids.Count.ShouldBe(1, $"page {page} holds one row under limit=1");
            seen.AddRange(ids.Select(id => id.ToString("D")));

            var next = answer.Body.GetProperty("next_cursor");
            if (page < 2)
            {
                next.ValueKind.ShouldBe(JsonValueKind.String, "a page that is not the last carries a cursor");
                cursor = next.GetString();
            }
            else
            {
                next.ValueKind.ShouldBe(JsonValueKind.Null, "the last page carries no cursor");
            }
        }

        seen.ShouldBe(expected, "every template once, in (created_at, id) order");
        seen.Distinct(StringComparer.Ordinal).Count().ShouldBe(3, "no row is served twice across the tie");
    }

    [Fact]
    public async Task List_FilteredContinuation_SkipsAnInactiveRowBetweenActiveOnes()
    {
        await using var world = await StartAsync();
        var first = await SeedAsync(world, kind: "web", createdAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedAsync(world, kind: "web", active: false, createdAt: new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        await SeedAsync(world, kind: "api", createdAt: new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
        var last = await SeedAsync(world, kind: "web", createdAt: new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero));

        var pageOne = await ListAsync(world, "?kind=web&limit=1");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, pageOne.ErrorMessage);
        Ids(pageOne).ShouldBe([first], "the inactive row and the other kind are filtered out of the first page");

        var cursor = pageOne.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace("a second active web row follows");

        var pageTwo = await ListAsync(world, $"?kind=web&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        pageTwo.Status.ShouldBe(HttpStatusCode.OK, pageTwo.ErrorMessage);
        Ids(pageTwo).ShouldBe([last], "the continuation keeps the kind filter and never resurfaces the inactive row");
        pageTwo.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null, "no third active web row follows");
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
    public async Task ListAndDetail_RunOneStatementOverTheTemplateTable()
    {
        await using var world = await StartAsync();
        var first = await SeedAsync(world, createdAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedAsync(world, createdAt: new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));

        world.Api.Statements.Clear();
        var pageOne = await ListAsync(world, "?limit=1");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, pageOne.ErrorMessage);
        TemplateStatements(world).ShouldBe(1, "one page is one statement; the limit+1 row decides has-more without a count");

        var cursor = pageOne.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace("a page that is not last carries a cursor");

        world.Api.Statements.Clear();
        (await ListAsync(world, $"?limit=1&cursor={Uri.EscapeDataString(cursor!)}")).Status
            .ShouldBe(HttpStatusCode.OK, "the cursor page is served");
        TemplateStatements(world).ShouldBe(1, "a continuation page is the same single statement");

        world.Api.Statements.Clear();
        (await GetAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK, "the detail read is served");
        TemplateStatements(world).ShouldBe(1, "the detail read is one statement, rules document included");
    }

    /// <summary>The statements of one request that read the template table.</summary>
    private static int TemplateStatements(World world) =>
        world.Api.Statements.Snapshot().Count(statement =>
            statement.Contains("from public.template", StringComparison.Ordinal));

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
        var first = await SeedAsync(world, createdAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedAsync(world, createdAt: new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));

        var pageOne = await ListAsync(world, "?limit=1");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, "the probe host serves the same page");

        holder.Value.ShouldNotBeNull("the host resolved the probe over the tenant db");
        holder.Value.Reads.ShouldBe([true], "the list statement ran in a read-only transaction");
        var cursor = pageOne.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace("two rows over a page of one carry a cursor");

        holder.Value.Reads.Clear();
        (await ListAsync(world, $"?limit=1&cursor={Uri.EscapeDataString(cursor!)}")).Status
            .ShouldBe(HttpStatusCode.OK, "the probe host serves the cursor page");
        holder.Value.Reads.ShouldBe([true], "the cursor page's statement ran in a read-only transaction");

        holder.Value.Reads.Clear();
        (await GetAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK, "the probe host serves the detail");
        holder.Value.Reads.ShouldBe([true], "the detail statement ran in a read-only transaction");

        holder.Value.Writes.ShouldBe(0, "a read route never executes a write");
    }

    [Fact]
    public async Task List_PageSize_DefaultsToFiftyAndNeverExceedsTwoHundred()
    {
        await using var world = await StartAsync();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules, created_at)
            select gen_random_uuid(), null, 'web', 'bulk-' || n, 1, '{BodyRef}', jsonb_build_object(),
                   timestamptz '2026-05-01' + n * interval '1 second'
            from generate_series(1, 201) as n
            """);

        var defaultPage = await ListAsync(world);
        defaultPage.Status.ShouldBe(HttpStatusCode.OK, defaultPage.ErrorMessage);
        Ids(defaultPage).Count.ShouldBe(50, "an omitted limit returns fifty rows");
        defaultPage.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.String);

        var widest = await ListAsync(world, "?limit=200");
        widest.Status.ShouldBe(HttpStatusCode.OK, widest.ErrorMessage);
        Ids(widest).Count.ShouldBe(200, "the widest page returns two hundred of the 201 rows");
        var cursor = widest.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace("one row remains");

        var rest = await ListAsync(world, $"?limit=200&cursor={Uri.EscapeDataString(cursor!)}");
        Ids(rest).Count.ShouldBe(1, "the last row follows on the next page");
        rest.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
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
    public async Task Get_ServesARulesDocumentNestedAtTheDepthLimit()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world, rules: NestedRules(32));

        var answer = await GetAsync(world, id);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var rules = answer.Body.GetProperty("conformance_rules");
        for (var level = 0; level < 32; level++)
        {
            rules.ValueKind.ShouldBe(JsonValueKind.Array, $"level {level} is an array");
            rules.GetArrayLength().ShouldBe(1, $"level {level} holds one element");
            rules = rules[0];
        }

        rules.ValueKind.ShouldBe(JsonValueKind.String, "the innermost value is the scalar");
        rules.GetString().ShouldBe("leaf");
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
        private const string ReadOnlySql = "select current_setting('transaction_read_only') = 'on'";

        // Principal and tenant resolution read through the same db before the handler runs; only the
        // template statements are the subject of the probe.
        public List<bool> Reads { get; } = [];

        public int Writes { get; private set; }

        public DbConnection Connection => inner.Connection;

        public DbTransaction Transaction => inner.Transaction;

        public async Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            await RecordAsync(sql, cancellationToken);
            return await inner.QueryAsync<T>(sql, parameters, cancellationToken);
        }

        public async Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            await RecordAsync(sql, cancellationToken);
            return await inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
        }

        private async Task RecordAsync(string sql, CancellationToken cancellationToken)
        {
            if (sql.Contains("public.template", StringComparison.Ordinal))
            {
                Reads.Add(await inner.ScalarAsync<bool>(ReadOnlySql, cancellationToken: cancellationToken));
            }
        }

        public Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (sql.Contains("public.template", StringComparison.Ordinal))
            {
                Writes++;
            }

            return inner.ExecuteAsync(sql, parameters, cancellationToken);
        }

        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.ScalarAsync<T>(sql, parameters, cancellationToken);
    }
}
