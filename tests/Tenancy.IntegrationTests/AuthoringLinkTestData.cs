using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

internal sealed record AuthoringLinkTree(Guid Org, Guid Project, Guid Item)
{
    internal string CasesPath => $"/v1/projects/{Project:D}/authoring/manual-cases";

    internal Action<HttpRequestHeaders> Caller { get; } = TenancyApiFactory.Caller(org: Org, user: Guid.CreateVersion7());

    internal string LinkPath(Guid manualCase) => $"{CasesPath}/{manualCase:D}/links";
}

internal static class AuthoringLinkTestData
{
    internal static async Task<AuthoringLinkTree> SeedTreeAsync(PostgresTenancyFixture fixture, string database, Guid? orgId = null)
    {
        var org = orgId ?? Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        if (orgId is null)
        {
            await fixture.ExecuteAsync(database,
                $"insert into public.organization (id, name, slug) values ('{org}', 'Link tests', 'link-{org:N}')");
        }

        await fixture.ExecuteAsync(database, $"""
            insert into public.project (id, org_id, name, slug)
            values ('{project}', '{org}', 'Link project', 'link-{project:N}');
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{node}', '{org}', '{project}', 'module', 'Links', 0);
            insert into public.checklist_item
                (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{item}', '{org}', '{project}', '{node}', 'Coverage', 'high', 'active', 'manual', 0);
            """);
        return new AuthoringLinkTree(org, project, item);
    }

    internal static async Task<Guid> SeedManualCaseAsync(
        PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree, int number = 1)
    {
        var id = Guid.CreateVersion7();
        var code = "LINK-" + number.ToString("D3", CultureInfo.InvariantCulture);
        await fixture.ExecuteAsync(database, $$"""
            insert into public.manual_test_case
                (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            values ('{{id}}', '{{tree.Org}}', '{{tree.Project}}', '{{tree.Item}}', '{{code}}', 'Manual link case',
                '{"steps":[{"no":1,"action":"Open","expected":"Shown"}]}', 'high', 'draft', 'manual');
            """);
        return id;
    }

    internal static async Task<Guid> SeedCatalogCaseAsync(
        PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree)
    {
        var id = Guid.CreateVersion7();
        await fixture.ExecuteAsync(database, $"""
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values ('{id}', '{tree.Org}', '{tree.Project}', '{Guid.CreateVersion7()}',
                    'Catalog link case', 'playwright', 'abcdef0');
            """);
        return id;
    }

    internal static Task ArchiveAsync(PostgresTenancyFixture fixture, string database, Guid catalogCase) =>
        fixture.ExecuteAsync(database, $"update public.test_case set status = 'archived' where id = '{catalogCase}'");

    internal static async Task<ApiAnswer> LinkAsync(HttpClient client, AuthoringLinkTree tree, Guid manualCase, Guid catalogCase)
    {
        var answer = await client.SendAsync(HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller,
            new { test_case_id = catalogCase }, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Id("link_id").ShouldNotBe(Guid.Empty);
        return answer;
    }

    internal static Task<int> LinkCountAsync(PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree) =>
        fixture.ScalarAsync<int>(database,
            $"select count(*)::int from public.manual_case_link where org_id = '{tree.Org}' and project_id = '{tree.Project}'");
}
