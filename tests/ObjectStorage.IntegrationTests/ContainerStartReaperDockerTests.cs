using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using Evidgate.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// Recovery of the reaper start against a real Docker daemon. The reaper name is unique per test, so the
/// process's own Ryuk is never touched; the stale container is created and never started.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ContainerStartReaperDockerTests : IAsyncLifetime
{
    private static readonly ContainerStartOptions Fast = new(
        MaxAttempts: 4,
        AttemptTimeout: TimeSpan.FromSeconds(30),
        Budget: TimeSpan.FromMinutes(2),
        Delay: TimeSpan.FromMilliseconds(100),
        DisposeTimeout: TimeSpan.FromSeconds(30));

    private readonly string _name = $"evidgate-ryuk-test-{Guid.NewGuid():N}";

    private readonly List<string> _log = [];

    private DockerClient _docker = null!;

    public async ValueTask InitializeAsync()
    {
        var auth = TestcontainersSettings.OS.DockerEndpointAuthConfig;
        _docker = auth.GetDockerClientBuilder(Guid.NewGuid()).Build();
        await EnsureImageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await RemoveQuietlyAsync(_name);
        _docker.Dispose();
    }

    [Fact]
    public async Task StaleCreatedContainer_IsRemovedByTheQuotedIdAndTheReaperStarts()
    {
        var staleId = await CreateAsync(TestContext.Current.CancellationToken);
        (await StateOfAsync(staleId)).ShouldBe("created");
        var conflicts = 0;

        await ContainerStart.EnsureResourceReaperAsync(
            _name,
            () => !Started,
            token => StartReaperAsync(() => conflicts++, token),
            (conflictingId, token) =>
            {
                conflictingId.ShouldBe(staleId);
                return ContainerStart.RemoveReaperContainerAsync(_docker.Containers, _name, conflictingId, Record, token);
            },
            Fast,
            Record,
            TestContext.Current.CancellationToken);

        conflicts.ShouldBe(1);
        await ShouldBeGoneAsync(staleId);
        var reaper = await _docker.Containers.InspectContainerAsync(_name, TestContext.Current.CancellationToken);
        reaper.ID.ShouldNotBe(staleId);
        reaper.State?.Status.ShouldBe("running");
        _log.ShouldContain(line => line.Contains("removed stale reaper container by id " + staleId));
        _log.ShouldContain(line => line.Contains("reaper attempt 1/4 failed") && line.Contains($"removing {_name} and retrying"));
    }

    [Fact]
    public async Task RemoveAnswering404ThenTheNameStays409_RemovesByIdOnTheNextRound()
    {
        var staleId = await CreateAsync(TestContext.Current.CancellationToken);
        // dockerd still registering the container: the first round's id and name removes both answer 404.
        var containers = Substitute.For<IContainerOperations>();
        containers.RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(call => RemoveOrAnswer404(call.ArgAt<string>(0), call.ArgAt<ContainerRemoveParameters>(1), call.ArgAt<CancellationToken>(2)));
        // The name is not inspectable yet either while dockerd registers it: the 409 is the only proof it is taken.
        containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Volatile.Read(ref _removes) <= 2
                ? throw new DockerContainerNotFoundException(System.Net.HttpStatusCode.NotFound, "No such container")
                : _docker.Containers.InspectContainerAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));

        await ContainerStart.RemoveReaperContainerAsync(
            containers, _name, staleId, Record, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(50));

        await ShouldBeGoneAsync(staleId);
        _removes.ShouldBe(4); // two injected 404s, the id removal, then the name already gone
        var notFound = _log.FindAll(line => line.Contains("(404)"));
        notFound.Count.ShouldBeGreaterThanOrEqualTo(2);
        _log.ShouldContain(line => line.Contains("removed stale reaper container by id " + staleId));
        _log.FindIndex(line => line.Contains("(404)")).ShouldBeLessThan(_log.FindIndex(line => line.Contains("removed stale")));
    }

    private int _removes;

    private bool Started { get; set; }

    private async Task RemoveOrAnswer404(string target, ContainerRemoveParameters parameters, CancellationToken token)
    {
        if (Interlocked.Increment(ref _removes) <= 2)
        {
            throw new DockerContainerNotFoundException(System.Net.HttpStatusCode.NotFound, "No such container");
        }

        await _docker.Containers.RemoveContainerAsync(target, parameters, token);
    }

    private async Task StartReaperAsync(Action onConflict, CancellationToken token)
    {
        try
        {
            var id = await CreateAsync(token);
            await _docker.Containers.StartContainerAsync(id, new ContainerStartParameters(), token);
            Started = true;
        }
        catch (DockerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            onConflict();
            ContainerStart.IsNameConflict(exception, _name).ShouldBeTrue();
            throw;
        }
    }

    private async Task<string> CreateAsync(CancellationToken token = default)
    {
        var created = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters { Name = _name, Image = ContainerImages.Postgres, Cmd = ["sleep", "300"] }, token);
        return created.ID;
    }

    private async Task EnsureImageAsync()
    {
        try
        {
            await _docker.Images.InspectImageAsync(ContainerImages.Postgres, TestContext.Current.CancellationToken);
        }
        catch (DockerImageNotFoundException)
        {
            await _docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = ContainerImages.Postgres },
                null,
                new Progress<JSONMessage>(),
                TestContext.Current.CancellationToken);
        }
    }

    private async Task<string?> StateOfAsync(string id) => (await _docker.Containers.InspectContainerAsync(id, TestContext.Current.CancellationToken)).State?.Status;

    private async Task ShouldBeGoneAsync(string id) =>
        await Should.ThrowAsync<DockerContainerNotFoundException>(() => _docker.Containers.InspectContainerAsync(id, TestContext.Current.CancellationToken));

    private async Task RemoveQuietlyAsync(string id)
    {
        try
        {
            await _docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerApiException)
        {
        }
    }

    private void Record(string message)
    {
        lock (_log)
        {
            _log.Add(message);
        }
    }
}
