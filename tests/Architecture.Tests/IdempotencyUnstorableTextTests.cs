using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Pipeline.Behaviours;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Sample.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// A parsed body reaches the idempotency stage as a <see cref="JsonElement"/>, which can hold an unpaired
/// surrogate the writer refuses: that fault is answered 400 <c>request_invalid</c> before the key is claimed,
/// while any other serialisation failure stays a server error and a storable body keeps its request identity.
/// </summary>
public sealed class IdempotencyUnstorableTextTests
{
    private const string RefusalMessage = "the request body carries text or nesting that cannot be processed";

    [Fact]
    public async Task Handle_LoneSurrogate_InMemberName_RefusesWithoutClaimingTheKey()
    {
        using var document = JsonDocument.Parse("""{"\ud800do-not-echo":1}""");

        await AssertRefusedAsync(
            new IdempotentBodyCommand("key-name", document.RootElement.Clone()),
            Substitute.For<IIdempotencyStore>());
    }

    [Fact]
    public async Task Handle_LoneSurrogate_InStringValue_RefusesWithoutClaimingTheKey()
    {
        using var document = JsonDocument.Parse("""{"note":"prefix-do-not-echo\ud800"}""");

        await AssertRefusedAsync(
            new IdempotentBodyCommand("key-value", document.RootElement.Clone()),
            Substitute.For<IIdempotencyStore>());
    }

    [Fact]
    public async Task Handle_LoneSurrogate_InArrayElement_RefusesWithoutClaimingTheKey()
    {
        using var document = JsonDocument.Parse("""{"items":["do-not-echo\ud800"]}""");

        await AssertRefusedAsync(
            new IdempotentBodyCommand("key-array", document.RootElement.Clone()),
            Substitute.For<IIdempotencyStore>());
    }

    [Fact]
    public async Task Handle_LoneSurrogate_AtDepthSixty_InMemberName_RefusesWithoutClaimingTheKey()
    {
        // A parser accepts member names 60 containers deep, so the detector must reach that far too.
        var body = string.Concat(Enumerable.Repeat("{\"a\":", 59))
            + """{"\ud800do-not-echo":1}"""
            + new string('}', 59);
        using var document = JsonDocument.Parse(body);

        await AssertRefusedAsync(
            new IdempotentBodyCommand("key-deep", document.RootElement.Clone()),
            Substitute.For<IIdempotencyStore>());
    }

    [Fact]
    public async Task Handle_CleanMemberName_AtDepthSixty_ProceedsAndClaimsTheKey()
    {
        // The positive control for the depth walk: the same shape with a storable name is accepted.
        var body = string.Concat(Enumerable.Repeat("{\"a\":", 59))
            + """{"clean":1}"""
            + new string('}', 59);
        using var document = JsonDocument.Parse(body);
        var command = new IdempotentBodyCommand("key-deep-clean", document.RootElement.Clone());
        var store = ProceedingStore();

        var (response, calls) = await ProceedAsync(command, store);

        response.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(1);
        ReceivedBeginCount(store).ShouldBe(1);
    }

    [Fact]
    public async Task Handle_LoneSurrogate_InNullableElementOfNestedList_RefusesWithoutClaimingTheKey()
    {
        using var lone = JsonDocument.Parse("""{"\ud800do-not-echo":1}""");
        var command = new IdempotentNestedCommand(
            "key-nested",
            [
                new JsonParamsMember("m0", null),
                new JsonParamsMember("m1", lone.RootElement.Clone()),
            ]);

        await AssertRefusedAsync(command, Substitute.For<IIdempotencyStore>());
    }

    [Fact]
    public async Task Handle_CleanNullableElementOfNestedList_ProceedsAndClaimsTheKey()
    {
        // The positive control for the list walk: a storable element beside a null one is accepted.
        using var clean = JsonDocument.Parse("""{"ok":1}""");
        var command = new IdempotentNestedCommand(
            "key-nested-clean",
            [
                new JsonParamsMember("m0", null),
                new JsonParamsMember("m1", clean.RootElement.Clone()),
            ]);
        var store = ProceedingStore();

        var (response, calls) = await ProceedAsync(command, store);

        response.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(1);
        ReceivedBeginCount(store).ShouldBe(1);
    }

    [Fact]
    public async Task Handle_AstralKeyAndNulValue_HandsTheSerializedCommandToTheStore()
    {
        // These are carryable text: the writer itself emits the surrogate pair and the NUL escape, so the
        // request must proceed with the very bytes the serializer produced.
        const string Expected =
            """{"IdempotencyKey":"json-key","Body":{"\uD83D\uDE80":"astral","value":"before\u0000after"}}""";
        using var document = JsonDocument.Parse("""{"\ud83d\ude80":"astral","value":"before\u0000after"}""");
        var command = new IdempotentBodyCommand("json-key", document.RootElement.Clone());
        var store = ProceedingStore();

        var (response, calls) = await ProceedAsync(command, store);

        response.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(1);

        var handed = HandedJson(store);
        handed.ShouldBe(Expected);
        CanonicalHash.Compute(handed)
            .ShouldBe("sha256:7101e4bbed9054a674229bca991937afce6ebae1c688b8f22334b70a4dd66309");
    }

    /// <summary>
    /// The hashes were captured from <c>JsonSerializer.Serialize</c> plus <c>CanonicalHash.Compute</c>
    /// on the tree that predates the malformed-text guard, so any drift in the stored request identity
    /// fails here instead of silently splitting dedupe for retries.
    /// </summary>
    [Theory]
    [InlineData("""{}""", "sha256:912d6331947d9829c5d1e6c139e32a3c0b229711855c256103db1812e64bad48")]
    [InlineData("""{"outer":{"enabled":true,"items":[1,2]}}""", "sha256:77b5955595077c80a077a307a0f03684fce9f45ad0aaac8a8459bc0ed458352d")]
    [InlineData("""{"message":"Tiếng Việt Ω"}""", "sha256:b5061558d4037f38ab5e69b9c844eb2c60a0e29c7a1f81974ea7fa148887acb7")]
    [InlineData("""{"\ud83d\ude80":"astral"}""", "sha256:bc1c226f30cf5fb4af9b53d71047e2736f8d65467704551a11cab4784d832660")]
    [InlineData("""{"value":"before\u0000after"}""", "sha256:7a6a85ae30a18c7837fc92df899e4f84eb2b4f6404046ae2f5a2fddd464ed5e1")]
    [InlineData("""{"integer":42,"negative":-17,"decimal":12.5,"zero":0}""", "sha256:d49ffea42e7d28679bb55cadf048a2372bc6bcd169a20275b7de209e2a97de70")]
    [InlineData("""[1,"two",false,{"three":[3]}]""", "sha256:2f2745f9ede6b5b32d6911c0492d922c9b59f4770981c2d653b5e3d7b38a2c31")]
    [InlineData("""{"missing":null,"present":true}""", "sha256:9d26cca0f33265bce606d3b052e311e0722d06ef35b6879644022909e74a9ce2")]
    [InlineData("""{"quote":"say \"hello\"","slash":"a\\b"}""", "sha256:da4e6fb654a4f08e78fbba2823db981e52bd1fb5d14f48851fb81cd9cc5729a3")]
    public async Task Handle_SerializableBody_KeepsTheBaseRequestIdentity(string body, string expectedHash)
    {
        using var document = JsonDocument.Parse(body);
        var command = new IdempotentBodyCommand("json-key", document.RootElement.Clone());
        var store = ProceedingStore();

        await ProceedAsync(command, store);

        var handed = HandedJson(store);
        handed.ShouldBe(JsonSerializer.Serialize(command));
        CanonicalHash.Compute(handed).ShouldBe(expectedHash);
    }

    [Fact]
    public async Task Handle_SerializationFailsForAnotherReason_LetsTheJsonExceptionEscape()
    {
        // A reference cycle is a bug in the command, not caller text: the guard must not answer it as a 400.
        var node = new CyclicNode("root");
        node.Self = node;
        using var services = new ServiceCollection().BuildServiceProvider();
        var command = new IdempotentCyclicCommand("key-cycle", node);
        var store = Substitute.For<IIdempotencyStore>();
        var calls = 0;

        var thrown = await Should.ThrowAsync<JsonException>(async () =>
        {
            await Behaviour<IdempotentCyclicCommand>(services, store).Handle(
                command,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult(Result.Success(new SampleDto()));
                },
                TestContext.Current.CancellationToken);
        });

        thrown.Message.ShouldContain("cycle");
        thrown.Message.ShouldNotContain(RefusalMessage);
        calls.ShouldBe(0);
        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NulBodyBeforeServerCycle_LetsTheOriginalJsonExceptionEscape()
    {
        using var document = JsonDocument.Parse("""{"note":"before\u0000after"}""");
        var node = new CyclicNode("root");
        node.Self = node;
        var command = new IdempotentCyclicBodyCommand("key-nul-cycle", document.RootElement.Clone(), node);
        var expected = Should.Throw<JsonException>(() => JsonSerializer.Serialize(command));
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = Substitute.For<IIdempotencyStore>();
        var calls = 0;

        var thrown = await Should.ThrowAsync<JsonException>(async () =>
        {
            await Behaviour<IdempotentCyclicBodyCommand>(services, store).Handle(
                command,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult(Result.Success(new SampleDto()));
                },
                TestContext.Current.CancellationToken);
        });

        thrown.Message.ShouldBe(expected.Message);
        thrown.Path.ShouldBe(expected.Path);
        calls.ShouldBe(0);
        store.ReceivedCalls().ShouldBeEmpty();

        var control = command with { ServerObject = new CyclicNode("root") };
        var controlStore = ProceedingStore();
        var (response, controlCalls) = await ProceedAsync(control, controlStore);
        response.IsSuccess.ShouldBeTrue();
        controlCalls.ShouldBe(1);
        ReceivedBeginCount(controlStore).ShouldBe(1);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    public async Task Handle_ParsedBodyAtWriterDepthBoundary_RefusesOnlyWrapperOverflow(int bodyDepth)
    {
        var body = new string('[', bodyDepth) + "0" + new string(']', bodyDepth);
        using var document = JsonDocument.Parse(body);
        var command = new IdempotentBodyCommand("key-depth", document.RootElement.Clone());

        if (bodyDepth == 64)
        {
            Should.Throw<JsonException>(() => JsonSerializer.Serialize(command));
            await AssertRefusedAsync(command, Substitute.For<IIdempotencyStore>());
        }
        else
        {
            var store = ProceedingStore();
            var (response, calls) = await ProceedAsync(command, store);
            response.IsSuccess.ShouldBeTrue();
            calls.ShouldBe(1);
            ReceivedBeginCount(store).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Handle_UndefinedBody_LetsTheInvalidOperationExceptionEscape()
    {
        var command = new IdempotentBodyCommand("key-undefined", default);
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = Substitute.For<IIdempotencyStore>();
        var calls = 0;

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await Behaviour<IdempotentBodyCommand>(services, store).Handle(
                command,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult(Result.Success(new SampleDto()));
                },
                TestContext.Current.CancellationToken);
        });

        calls.ShouldBe(0);
        store.ReceivedCalls().ShouldBeEmpty();

        using var document = JsonDocument.Parse("null");
        var controlStore = ProceedingStore();
        var (response, controlCalls) = await ProceedAsync(
            command with { Body = document.RootElement.Clone() }, controlStore);
        response.IsSuccess.ShouldBeTrue();
        controlCalls.ShouldBe(1);
        ReceivedBeginCount(controlStore).ShouldBe(1);
    }

    [Theory]
    [InlineData("""[{"clean":1},{"\ud800do-not-echo":2}]""", """[{"clean":1},{"name":2}]""")]
    [InlineData("""[{"clean":1},{"note":"do-not-echo\ud800"}]""", """[{"clean":1},{"note":"ok"}]""")]
    public async Task Handle_LoneSurrogateBesideCleanObject_RefusesOnlyTheMalformedBody(
        string malformed,
        string clean)
    {
        using var malformedDocument = JsonDocument.Parse(malformed);
        await AssertRefusedAsync(
            new IdempotentBodyCommand("key-adjacent", malformedDocument.RootElement.Clone()),
            Substitute.For<IIdempotencyStore>());

        using var cleanDocument = JsonDocument.Parse(clean);
        var store = ProceedingStore();
        var (response, calls) = await ProceedAsync(
            new IdempotentBodyCommand("key-adjacent", cleanDocument.RootElement.Clone()), store);
        response.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(1);
        ReceivedBeginCount(store).ShouldBe(1);
    }

    private static async Task AssertRefusedAsync<TMessage>(TMessage command, IIdempotencyStore store)
        where TMessage : IMessage
    {
        // The marker sits inside the offending name or value; the refusal must not echo it back.
        const string Marker = "do-not-echo";
        using var services = new ServiceCollection().BuildServiceProvider();
        var calls = 0;

        var response = await Behaviour<TMessage>(services, store).Handle(
            command,
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(Result.Success(new SampleDto()));
            },
            TestContext.Current.CancellationToken);

        response.IsSuccess.ShouldBeFalse();
        response.Status.ShouldBe(400);

        var error = response.Error.ShouldNotBeNull();
        error.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        error.Message.ShouldBe(RefusalMessage);
        error.Message.ShouldNotContain(Marker);

        var detail = error.Details.ShouldNotBeNull().ShouldHaveSingleItem();
        detail.Path.ShouldBe(string.Empty);
        detail.Reason.ShouldBe("invalid_value");

        calls.ShouldBe(0);
        store.ReceivedCalls().ShouldBeEmpty();
    }

    private static IdempotencyBehaviour<TMessage, Result<SampleDto>> Behaviour<TMessage>(
        IServiceProvider services,
        IIdempotencyStore store)
        where TMessage : IMessage =>
        new(services, store, Substitute.For<IAuditTrail>());

    /// <summary>Runs one command through the stage with a handler that succeeds, counting its invocations.</summary>
    private static async Task<(Result<SampleDto> Response, int Calls)> ProceedAsync<TMessage>(
        TMessage command,
        IIdempotencyStore store)
        where TMessage : IMessage
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var calls = 0;

        var response = await Behaviour<TMessage>(services, store).Handle(
            command,
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(Result.Success(new SampleDto()));
            },
            TestContext.Current.CancellationToken);

        return (response, calls);
    }

    private static IIdempotencyStore ProceedingStore()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.TryBeginAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new IdempotencyBeginResult(IdempotencyOutcome.Proceed, Guid.CreateVersion7()));

        return store;
    }

    /// <summary>The request document the stage handed to the store, exactly as the store received it.</summary>
    private static string HandedJson(IIdempotencyStore store)
    {
        var begin = store.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IIdempotencyStore.TryBeginAsync));

        return (string)begin.GetArguments()[2]!;
    }

    private static int ReceivedBeginCount(IIdempotencyStore store) =>
        store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IIdempotencyStore.TryBeginAsync));

    private sealed record IdempotentBodyCommand(string IdempotencyKey, JsonElement Body)
        : ICommand<Result<SampleDto>>, IIdempotentCommand;

    private sealed record IdempotentNestedCommand(string IdempotencyKey, IReadOnlyList<JsonParamsMember>? Members)
        : ICommand<Result<SampleDto>>, IIdempotentCommand;

    /// <summary>The wire shape of a suite member's parameters: a nullable parsed element inside a list.</summary>
    private sealed record JsonParamsMember(string Ref, JsonElement? Params);

    private sealed record IdempotentCyclicCommand(string IdempotencyKey, CyclicNode Body)
        : ICommand<Result<SampleDto>>, IIdempotentCommand;

    private sealed record IdempotentCyclicBodyCommand(string IdempotencyKey, JsonElement Body, CyclicNode ServerObject)
        : ICommand<Result<SampleDto>>, IIdempotentCommand;

    private sealed record CyclicNode(string Name)
    {
        public CyclicNode? Self { get; set; }
    }
}
