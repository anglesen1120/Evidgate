// The TRUNCATE half of the evaluation grants: the app role's table-wide ACL is what refuses it, and the
// refusal must leave every stored verdict in place while ordinary inserts keep working.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationSchemaTests
{
    /// <summary>
    /// <c>TRUNCATE</c> bypasses row level security, so the tenant policy cannot stand in for the grant: the app
    /// role is refused 42501 and the row survives, while its INSERT + SELECT still work.
    /// </summary>
    [Fact]
    public async Task Evaluation_AppRole_TruncateDenied()
    {
        // Dedicated: the attempt asks for a table-wide lock before the ACL refuses it, so it must not wait on
        // another suite's transaction over the shared database.
        await using var db = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: db.Name);
        var original = await SeedAsync(world);
        (await ScalarAsOrgAsync<long>(world, world.Org, "select count(*) from public.evaluation")).ShouldBe(1);

        (await CaptureAppErrorAsync(world, world.Org, "truncate public.evaluation"))
            .SqlState.ShouldBe("42501");

        (await ScalarAsOrgAsync<long>(world, world.Org, "select count(*) from public.evaluation"))
            .ShouldBe(1, "the refused truncate leaves the stored verdict in place");
        (await ScalarAsOrgAsync<long>(
            world,
            world.Org,
            $"select count(*) from public.evaluation where id = '{original.Id:D}'"))
            .ShouldBe(1);

        var inserted = await SeedAsync(world);
        (await ScalarAsOrgAsync<long>(world, world.Org, "select count(*) from public.evaluation")).ShouldBe(2);
        (await ScalarAsOrgAsync<long>(
            world,
            world.Org,
            $"select count(*) from public.evaluation where id = '{inserted.Id:D}'"))
            .ShouldBe(1, "the app role still inserts and selects a second committed evaluation");
    }
}
