using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The binding write unit's installation proof against a real database and two connections: the proof holds a share
/// lock, so a disconnect either commits first (the binding is refused) or waits for the binding to commit. Either order
/// leaves no active binding that the disconnect did not see.
/// </summary>
[Trait("Category", "Docker")]
public sealed class GitRepoBindingInstallationLockTests(PostgresTenancyFixture fixture)
{
    private const string BindingInsert = """
        insert into public.git_repo_binding
            (id, org_id, project_id, provider, installation_id, repository_id, default_branch, webhook_secret_ref)
        values (@id, @org_id, @project_id, 'github', @installation_id, '4242', 'main', @secret)
        """;

    private readonly PostgresTenancyFixture _fixture = fixture;

    private sealed record Seeded(string Database, Guid Org, Guid Project, Guid Installation, long InstallationId);

    [Fact]
    public async Task DisconnectCommitsFirst_TheProofFindsNoRow_AndNoBindingIsWritten()
    {
        var seeded = await SeedAsync();

        await using (var disconnect = await BeginAsync(seeded))
        {
            (await disconnect.ScalarAsync<long?>(
                GitHubInstallationStore.DisconnectSql, ("org_id", seeded.Org), ("id", seeded.Installation)))
                .ShouldBe(seeded.InstallationId);
            await disconnect.CommitAsync();
        }

        await using (var bind = await BeginAsync(seeded))
        {
            (await bind.ScalarAsync<int?>(
                GitHubInstallationStore.LockActiveSql,
                ("org_id", seeded.Org),
                ("installation_id", seeded.InstallationId))).ShouldBeNull("a deleted installation is refused");
        }

        (await BindingCountAsync(seeded)).ShouldBe(0);
    }

    [Fact]
    public async Task DisconnectArrivesAfterTheProof_WaitsForTheBindingToCommit()
    {
        var seeded = await SeedAsync();

        await using var bind = await BeginAsync(seeded);
        (await bind.ScalarAsync<int?>(
            GitHubInstallationStore.LockActiveSql,
            ("org_id", seeded.Org),
            ("installation_id", seeded.InstallationId))).ShouldBe(1);

        var disconnect = Task.Run(
            async () =>
            {
                await using var scope = await BeginAsync(seeded);
                var moved = await scope.ScalarAsync<long?>(
                    GitHubInstallationStore.DisconnectSql, ("org_id", seeded.Org), ("id", seeded.Installation));
                await scope.CommitAsync();

                return moved;
            },
            TestContext.Current.CancellationToken);

        await AwaitParkedAsync(seeded.Database, disconnect);

        var binding = Guid.CreateVersion7();
        await bind.ExecuteAsync(
            BindingInsert,
            ("id", binding),
            ("org_id", seeded.Org),
            ("project_id", seeded.Project),
            ("installation_id", seeded.InstallationId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("secret", $"secret:gh_wh_{binding:N}"));
        await bind.CommitAsync();

        (await disconnect).ShouldBe(seeded.InstallationId, "the disconnect applies once the binding has committed");
        (await BindingCountAsync(seeded)).ShouldBe(1);
        (await InstallationStateAsync(seeded)).ShouldBe("deleted");
    }

    private Task<TenantScope> BeginAsync(Seeded seeded) =>
        TenantScope.BeginAsync(_fixture.AppConnectionString(seeded.Database), seeded.Org);

    /// <summary>Waits until another session of the database is blocked on a lock; the disconnect must not finish first.</summary>
    private async Task AwaitParkedAsync(string database, Task<long?> disconnect)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await _fixture.ScalarAsync<int>(
                   database,
                   "select count(*)::int from pg_stat_activity where datname = current_database() "
                   + "and wait_event_type = 'Lock' and pid <> pg_backend_pid() and query ilike '%github_installation%'") == 0)
        {
            disconnect.IsCompleted.ShouldBeFalse("the disconnect finished while the binding transaction held the proof");
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the disconnect never waited on the installation row");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        disconnect.IsCompleted.ShouldBeFalse();
    }

    private async Task<Seeded> SeedAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var installation = Guid.CreateVersion7();
        var installationId = Random.Shared.NextInt64(1, long.MaxValue);

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'binding lock', '{Unique.Slug("org")}');
            insert into public.project (id, org_id, name, slug)
            values ('{project:D}', '{org:D}', 'binding lock', '{Unique.Slug("project")}');
            insert into public.github_installation (id, org_id, installation_id, account_login, account_id, permissions, state)
            values ('{installation:D}', '{org:D}', {installationId}, 'octo', 1, jsonb_build_object(), 'active');
            """);

        return new Seeded(database, org, project, installation, installationId);
    }

    private Task<long> BindingCountAsync(Seeded seeded) =>
        _fixture.ScalarAsync<long>(
            seeded.Database,
            $"select count(*) from public.git_repo_binding where org_id = '{seeded.Org:D}'");

    private Task<string> InstallationStateAsync(Seeded seeded) =>
        _fixture.ScalarAsync<string>(
            seeded.Database,
            $"select state from public.github_installation where id = '{seeded.Installation:D}'");
}
