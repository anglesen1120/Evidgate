using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T3's statement budget: a restore is a bounded number of statements, one of them the sibling group's advisory
/// lock (hand-over (a), OI-13-128) — never a per-sibling loop and never a second append path. The create side of the
/// counter lives in <c>AuthoringCommandCounterTests.cs</c>.
/// </summary>
/// <remarks>
/// The count is taken from the host's Npgsql command log (<c>TenancyApiFactory.Statements</c>), so it measures what the
/// database was actually asked to do through the real pipeline — tenant resolution, the permission gate, the command and
/// the writer — and not what one layer believes it issues.
/// </remarks>
public sealed partial class AuthoringCommandCounterTests
{
    /// <summary>
    /// One <c>POST /nodes/{n}/restore</c>: a bounded read side, one entity update, exactly one version append, one audit
    /// row, one idempotency claim and exactly one group lock.
    /// </summary>
    [Fact]
    public async Task Restore_StaysWithinBudget()
    {
        var database = await Fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(Fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        Action<HttpRequestHeaders> caller = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var nodes = $"/v1/projects/{project}/authoring/nodes";

        // The tombstone the restore re-appends, produced by the two routes that own it (create, delete).
        var created = await client.SendAsync(HttpMethod.Post, nodes, caller, new { kind = "module", title = "Checkout" }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var node = created.Id("node_id");

        var deleted = await client.SendAsync(
            HttpMethod.Delete, $"{nodes}/{node:D}", caller, idempotencyKey: NewKey(), ifMatch: "\"1\"");
        deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.ErrorMessage);

        api.Statements.Clear();

        var restored = await client.SendAsync(
            HttpMethod.Post, $"{nodes}/{node:D}/restore", caller, idempotencyKey: NewKey(), ifMatch: "\"2\"");

        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        restored.Body.GetProperty("version").GetInt32().ShouldBe(3);

        var statements = api.Statements.Snapshot();

        // The module's own statements, as the catalog budget test counts them (CatalogSuiteTests.cs:700): the pipeline's
        // tenant and permission reads stay in, so a hidden N+1 in either shows up here too.
        var writes = statements.Where(statement => statement.Contains("public.", StringComparison.Ordinal)).ToList();
        writes.Count.ShouldBeLessThanOrEqualTo(20, string.Join(Environment.NewLine, statements));

        // One append path (AC8), one entity write, one audit row (AC18), one claim — never one per sibling.
        statements.Count(statement => statement.Contains("insert into public.authoring_version", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("update public.feature_node", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("insert into public.audit_log", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("insert into public.idempotency_record", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));

        // Hand-over (a): the sibling group's advisory lock is taken exactly once, on the root group of this node. The
        // count is the group lock's own statement (`AuthoringSql.LockSiblingGroup`, the `hashtextextended` key), because
        // the audit chain takes its own advisory lock on a different key (`AuditTrail.AdvisoryLock`, `hashtext('audit:'…)`)
        // with every audit row, and counting both would say nothing about the per-sibling loop this test forbids.
        statements.Count(statement => statement.Contains("pg_advisory_xact_lock(hashtextextended", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("pg_advisory_xact_lock(hashtext('audit:'", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
    }
}
