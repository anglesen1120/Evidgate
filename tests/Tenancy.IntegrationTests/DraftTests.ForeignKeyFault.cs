using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Fault injection in a dedicated database: a plpgsql trigger raises a foreign-key violation (23503) with a chosen
/// constraint name at the insert, standing in for the project or the author's membership vanishing between the
/// handler's probe and its save. No production seam exists for that window, so the test cannot interleave two requests.
/// </summary>
public sealed partial class DraftTests
{
    private const string FaultTrigger = "draft_fk_fault";

    [Theory]
    [InlineData("draft_author_fk", HttpStatusCode.Forbidden, "draft_author_required")]
    [InlineData("draft_project_fk", HttpStatusCode.NotFound, "not_found")]
    public async Task Create_ForeignKeyViolationAtSave_IsAnsweredByTheConstraintThatFired(
        string constraint, HttpStatusCode status, string code)
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        await InjectForeignKeyFaultAsync(world, constraint);

        var refused = await CreateAsync(world);

        refused.Status.ShouldBe(status, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe(code);
        (await DraftCountAsync(world)).ShouldBe(0);

        // Positive control: without the fault the same caller creates, so the refusal came from the constraint name.
        await _fixture.ExecuteAsync(world.Database, $"drop trigger {FaultTrigger} on public.draft");
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_ForeignKeyViolationOfAnotherConstraint_IsNotMislabelled_AndSurfacesAsAServerFault()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        await InjectForeignKeyFaultAsync(world, "draft_linked_case_fk");

        var answer = await CreateAsync(world);

        answer.Status.ShouldBe(HttpStatusCode.InternalServerError, answer.ErrorMessage);
        (await DraftCountAsync(world)).ShouldBe(0);

        // Positive control: without the fault the same caller creates, so the 500 came from the unexpected constraint.
        await _fixture.ExecuteAsync(world.Database, $"drop trigger {FaultTrigger} on public.draft");
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    private Task InjectForeignKeyFaultAsync(World world, string constraint) =>
        _fixture.ExecuteAsync(world.Database, $"""
            create function public.draft_fk_fault() returns trigger language plpgsql as $$
            begin
              raise exception 'injected' using errcode = '23503', constraint = '{constraint}';
            end $$;
            create trigger {FaultTrigger} before insert on public.draft
              for each row execute function public.draft_fk_fault();
            """);
}
