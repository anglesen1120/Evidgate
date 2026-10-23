using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Isolation.Tests;

/// <summary>
/// 01.6.1-T4.2 — <b>test-only harness route; real /v1 routes inherit this check when
/// 02.x lands</b>. The smallest ASP.NET Core surface that runs a tenant-scoped read
/// through the production path — a real <see cref="ITenantScope"/> opened inside the
/// request flow and the real <see cref="ITenantDb"/> Dapper read — behind ordinary
/// in-process hosting (Kestrel on a loopback port, per the tests/README rule that an
/// integration project may start Kestrel listeners on purpose).
/// <para>
/// What is stand-in and what is real, so the check is read correctly:
/// </para>
/// <list type="bullet">
///   <item>
///   Stand-in: the tenant comes from the <see cref="TenantHeader"/> header, the slot
///   the authenticated principal occupies when 02.x resolves it before the pipeline.
///   A request without it gets 401 — a wrong header is not tenant data.
///   </item>
///   <item>
///   Real: <c>readOnlyTransaction: true</c> (the query scope of 00 §5.5 C1/T95), the
///   app-role data source, <c>evidgate.current_org_id()</c> through
///   <c>set_config</c>, and a statement with no <c>org_id</c> predicate — row level
///   security supplies the filter, exactly as the module's
///   <c>TenancyReadSql.ProjectById</c> does.
///   </item>
///   <item>
///   Contract: 00 §9.2 — an id of another org is a missing resource: 404, never 403
///   and never a body.
///   </item>
/// </list>
/// </summary>
internal sealed class TestOnlyTenantApi : IAsyncDisposable
{
    /// <summary>The test header standing in for the authenticated tenant (02.x replaces it with the principal).</summary>
    internal const string TenantHeader = "X-Evidgate-Test-Org";

    private readonly WebApplication _app;

    private TestOnlyTenantApi(WebApplication app) => _app = app;

    /// <summary>
    /// Builds the host over the harness's app-role data source — the same
    /// <c>NpgsqlDataSource</c> + <c>AddEvidgateTenancy</c> composition the fixture
    /// and the production hosts use — and starts Kestrel on an OS-assigned loopback
    /// port (two suites in one CI job can never collide).
    /// </summary>
    internal static async Task<TestOnlyTenantApi> StartAsync(NpgsqlDataSource appDataSource)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(appDataSource);
        builder.Services.AddEvidgateTenancy();

        var app = builder.Build();
        app.MapGet("/v1/projects/{id:guid}", GetProjectAsync);
        await app.StartAsync();

        return new TestOnlyTenantApi(app);
    }

    /// <summary>An unauthenticated-by-design client; the caller adds the tenant header per request.</summary>
    internal HttpClient CreateClient() => new() { BaseAddress = new Uri(_app.Urls.First(), UriKind.Absolute) };

    /// <summary>
    /// The test-only handler of <c>GET /v1/projects/{id}</c>: opens the read-only
    /// tenant scope for the header's org — inside the request's async flow, which is
    /// where the ambient scope must live — and answers 404 when row level security
    /// hides the row, which is also what a missing id returns.
    /// </summary>
    private static async Task<IResult> GetProjectAsync(
        Guid id,
        HttpContext http,
        ITenantScopeFactory scopes,
        ITenantDb db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(http.Request.Headers[TenantHeader].ToString(), out var orgId))
        {
            return Results.Unauthorized();
        }

        await using var scope = await scopes.BeginAsync(
            orgId,
            readOnlyTransaction: true,
            cancellationToken: cancellationToken);
        var project = await db.QuerySingleOrDefaultAsync<ProjectDto>(
            IsolationProbeSql.ProjectById,
            new { project_id = id },
            cancellationToken);

        return project is null ? Results.NotFound() : Results.Ok(project);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
