using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The repository binding's state column against a real migrated database: an installation event suspends and resumes a
/// binding by moving <c>sync_state</c> alone, the guard trigger refuses any rewrite of the row's identity, the app role
/// cannot delete a binding, and a tenant scope sees only its own rows.
/// </summary>
[Trait("Category", "Docker")]
public sealed class GitRepoBindingStateTests(PostgresTenancyFixture fixture)
{
    private const string UpdateState = "update public.git_repo_binding set sync_state = @sync_state where id = @binding";

    private const string Delete = "delete from public.git_repo_binding where id = @binding";

    private readonly PostgresTenancyFixture _fixture = fixture;

    private sealed record Seeded(string Database, Guid Org, Guid Project, Guid Binding);

    [Fact]
    public async Task Suspend_ThenResume_MovesTheOneColumn_AndKeepsEverythingElse()
    {
        var seeded = await SeedAsync();

        await MoveAsync(seeded, "disabled");
        await MoveAsync(seeded, "active");

        (await StateAsync(seeded)).ShouldBe("active");
        (await VersionAsync(seeded)).ShouldBe(3, "each accepted update bumps version once");
        (await RowCountAsync(seeded)).ShouldBe(1, "a suspension keeps the binding, its catalog and its history");
    }

    [Fact]
    public async Task Suspend_KeepsTheWebhookSecretReferenceAndTheRepository()
    {
        var seeded = await SeedAsync();
        var before = await IdentityAsync(seeded, "repository_id, webhook_secret_ref, default_branch");

        await MoveAsync(seeded, "disabled");

        (await IdentityAsync(seeded, "repository_id, webhook_secret_ref, default_branch")).ShouldBe(before);
        (await StateAsync(seeded)).ShouldBe("disabled");
    }

    [Fact]
    public async Task Suspend_KeepsTheBindingsSyncHistory()
    {
        var seeded = await SeedAsync();
        await SeedSyncAsync(seeded);

        await MoveAsync(seeded, "disabled");

        (await SyncCountAsync(seeded)).ShouldBe(1, "a suspension stops new syncs; the record of the finished ones stays");
    }

    [Fact]
    public async Task RewritingSyncStateToTheSameValue_IsAccepted()
    {
        // Positive control for the refused unknown value: the guard moves this column, it does not forbid updates.
        var seeded = await SeedAsync();

        await MoveAsync(seeded, "active");

        (await StateAsync(seeded)).ShouldBe("active");
        (await RowCountAsync(seeded)).ShouldBe(1);
    }

    [Theory]
    [InlineData("repository_id = '999999'", "repository_id")]
    [InlineData("webhook_secret_ref = 'secret:gh_wh_00000000000000000000000000000000'", "webhook_secret_ref")]
    [InlineData("installation_id = '424242'", "installation_id")]
    [InlineData("sync_state = 'suspended'", "sync_state")]
    public async Task RewritingTheIdentityOrAnUnknownState_IsRefused(string assignment, string column)
    {
        var seeded = await SeedAsync();

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            var error = await scope.CaptureErrorAsync(
                $"update public.git_repo_binding set {assignment} where id = @binding",
                ("binding", seeded.Binding));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, $"{column} must not move under the app role");
        }

        (await StateAsync(seeded)).ShouldBe("active");
        (await VersionAsync(seeded)).ShouldBe(1, "a refused update changes nothing");
    }

    [Fact]
    public async Task Delete_IsNotGrantedToTheAppRole()
    {
        var seeded = await SeedAsync();

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            var error = await scope.CaptureErrorAsync(Delete, ("binding", seeded.Binding));

            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await RowCountAsync(seeded)).ShouldBe(1);
    }

    [Fact]
    public async Task AnotherTenantsBinding_IsInvisibleUnderATenantScope()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var own = await SeedAsync(database);
        var other = await SeedAsync(database);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), own.Org))
        {
            (await scope.ExecuteAsync(UpdateState, ("sync_state", "disabled"), ("binding", other.Binding)))
                .ShouldBe(0, "RLS hides another org's binding from the scoped update");
            (await scope.ExecuteAsync(UpdateState, ("sync_state", "disabled"), ("binding", own.Binding)))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        (await StateAsync(other)).ShouldBe("active");
        (await StateAsync(own)).ShouldBe("disabled");
    }

    private async Task MoveAsync(Seeded seeded, string state)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(seeded.Database), seeded.Org);
        (await scope.ExecuteAsync(UpdateState, ("sync_state", state), ("binding", seeded.Binding))).ShouldBe(1);
        await scope.CommitAsync();
    }

    private async Task<Seeded> SeedAsync(string? database = null)
    {
        database ??= await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();
        var installation = Random.Shared.NextInt64(1, long.MaxValue);
        var repository = Random.Shared.NextInt64(1, long.MaxValue);

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'binding state', '{Unique.Slug("org")}');
            insert into public.project (id, org_id, name, slug)
            values ('{project:D}', '{org:D}', 'binding state', '{Unique.Slug("project")}');
            insert into public.git_repo_binding
                (id, org_id, project_id, installation_id, repository_id, default_branch, webhook_secret_ref)
            values
                ('{binding:D}', '{org:D}', '{project:D}', '{installation}', '{repository}', 'main',
                 'secret:gh_wh_{binding:N}');
            """);

        return new Seeded(database, org, project, binding);
    }

    private Task<string> StateAsync(Seeded seeded) =>
        _fixture.ScalarAsync<string>(seeded.Database, $"select sync_state from public.git_repo_binding where id = '{seeded.Binding:D}'");

    private Task<int> VersionAsync(Seeded seeded) =>
        _fixture.ScalarAsync<int>(seeded.Database, $"select version from public.git_repo_binding where id = '{seeded.Binding:D}'");

    private async Task SeedSyncAsync(Seeded seeded) =>
        await _fixture.ExecuteAsync(
            seeded.Database,
            $"""
            insert into public.catalog_sync (id, org_id, project_id, commit_sha, binding_id)
            values ('{Guid.CreateVersion7():D}', '{seeded.Org:D}', '{seeded.Project:D}', 'abc1234', '{seeded.Binding:D}');
            """);

    private Task<int> SyncCountAsync(Seeded seeded) =>
        _fixture.ScalarAsync<int>(
            seeded.Database,
            $"select count(*) from public.catalog_sync where binding_id = '{seeded.Binding:D}'");

    private Task<int> RowCountAsync(Seeded seeded) =>
        _fixture.ScalarAsync<int>(
            seeded.Database,
            $"select count(*) from public.git_repo_binding where org_id = '{seeded.Org:D}'");

    private Task<string> IdentityAsync(Seeded seeded, string columns) =>
        _fixture.ScalarAsync<string>(
            seeded.Database,
            $"select concat_ws('|', {columns}) from public.git_repo_binding where id = '{seeded.Binding:D}'");
}
