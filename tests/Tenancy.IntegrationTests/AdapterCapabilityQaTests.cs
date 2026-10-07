using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AdapterCapabilityQaTests(PostgresTenancyFixture fixture)
{
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "qa");
        return new World(api, client, database, user, org);
    }

    private static JsonObject Document() => JsonNode.Parse("""
        {"protocol":{"major":1,"minor":0},"engine":{"family":"playwright","version":"1.49.0"},
         "platforms":["linux-x64","linux-arm64"],
         "supports":{"discovery":true,"step_events":true,"retry":true,"cancel":true},"mode":"full"}
        """)!.AsObject();

    private static string Carrier(string schema, string document) =>
        "{\"kind\":\"run.log\",\"sequence\":1,\"ext\":{\"schema\":" + JsonSerializer.Serialize(schema) + ",\"capability\":" + document + "}}";

    private static async Task<Result<AdapterCapabilityRegistered>> RegisterAsync(World world, string document, Guid? org = null)
    {
        await using var scope = world.Api.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(org ?? world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(RequestPrincipal.Service(null, [Permissions.Event.Write]));
        return await provider.GetRequiredService<IMediator>().Send(new RegisterAdapterCapability("playwright", Encoding.UTF8.GetBytes(document)), TestContext.Current.CancellationToken);
    }

    private static async Task<Result<FirstSendCapabilityOutcome>> FirstSendAsync(World world, string envelope)
    {
        await using var scope = world.Api.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(RequestPrincipal.Service(null, [Permissions.Event.Write]));
        return await provider.GetRequiredService<IMediator>().Send(new RegisterFirstSendCapability(Encoding.UTF8.GetBytes(envelope)), TestContext.Current.CancellationToken);
    }

    private static void Invalid(Error? error)
    {
        error.ShouldNotBeNull();
        error.Code.ShouldBe(ApiErrorCode.CapabilityInvalid);
        error.Status.ShouldBe(422);
        error.Retryable.ShouldBeFalse();
        error.Details.ShouldNotBeNull();
        error.Details.ShouldNotBeEmpty();
        error.Details.ShouldAllBe(detail => detail.Path == string.Empty || detail.Path.StartsWith('/'));
    }

    [Theory]
    [InlineData("major", "0")]
    [InlineData("major", "2147483648")]
    [InlineData("major", "999999999999999999999999999999")]
    [InlineData("minor", "-1")]
    [InlineData("minor", "2147483648")]
    public async Task ProtocolBounds_Return422WithoutRows(string field, string number)
    {
        await using var world = await StartAsync();
        var document = Document();
        document["protocol"]![field] = JsonNode.Parse(number);
        var result = await RegisterAsync(world, document.ToJsonString());
        Invalid(result.Error);
        (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability")).ShouldBe(0);
    }

    [Theory]
    [InlineData("major", "1.0")]
    [InlineData("major", "1e0")]
    [InlineData("minor", "0.0")]
    public async Task IntegralJsonNumber_RegistersWithoutBindingException(string field, string number)
    {
        await using var world = await StartAsync();
        var document = Document();
        document["protocol"]![field] = JsonNode.Parse(number);
        var result = await RegisterAsync(world, document.ToJsonString());
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task NonBooleanSupports_Return422AndDoNotEchoValues(string value)
    {
        await using var world = await StartAsync();
        var document = Document();
        document["supports"]!["sensitive-marker"] = JsonNode.Parse(value);
        var result = await RegisterAsync(world, document.ToJsonString());
        Invalid(result.Error);
        JsonSerializer.Serialize(result.Error).ShouldNotContain("sensitive-marker");
        result.Error!.Details!.ShouldContain(detail => detail.Path == "/supports/*");
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public async Task SupportsMemberBoundary_IsEnforced(int count, bool accepted)
    {
        await using var world = await StartAsync();
        var document = Document();
        var supports = document["supports"]!.AsObject();
        for (var i = supports.Count; i < count; i++) supports[$"qa_flag_{i}"] = false;
        var result = await RegisterAsync(world, document.ToJsonString());
        result.IsSuccess.ShouldBe(accepted);
        if (!accepted) Invalid(result.Error);
    }

    [Fact]
    public async Task DuplicatePlatforms_Return422()
    {
        await using var world = await StartAsync();
        var document = Document();
        document["platforms"] = new JsonArray("linux-x64", "linux-x64");
        Invalid((await RegisterAsync(world, document.ToJsonString())).Error);
    }

    [Fact]
    public async Task ReorderedPlatforms_StoreSortedAndRemainIdentical()
    {
        await using var world = await StartAsync();
        var first = await RegisterAsync(world, Document().ToJsonString());
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        var document = Document();
        document["platforms"] = new JsonArray("linux-arm64", "linux-x64");
        var second = await RegisterAsync(world, document.ToJsonString());
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.Value.Id.ShouldBe(first.Value.Id);
        second.Value.Created.ShouldBeFalse();
        (await fixture.ScalarAsync<string>(world.Database, "select array_to_string(platforms, ',') from public.adapter_capability")).ShouldBe("linux-arm64,linux-x64");
    }

    [Theory]
    [InlineData("engine_family")]
    [InlineData("platforms")]
    [InlineData("supports")]
    [InlineData("mode")]
    public async Task DifferentNonKeyField_Returns409AndKeepsFirstRow(string field)
    {
        await using var world = await StartAsync();
        (await RegisterAsync(world, Document().ToJsonString())).IsSuccess.ShouldBeTrue();
        var document = Document();
        switch (field)
        {
            case "engine_family": document["engine"]!["family"] = "junit"; break;
            case "platforms": document["platforms"] = new JsonArray("windows-x64"); break;
            case "supports": document["supports"]!["retry"] = false; break;
            case "mode": document["mode"] = "ingest_only"; break;
        }
        var result = await RegisterAsync(world, document.ToJsonString());
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.CapabilityConflict);
        result.Error.Status.ShouldBe(409);
        result.Error.Retryable.ShouldBeFalse();
        (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability")).ShouldBe(1);
        (await fixture.ScalarAsync<string>(world.Database, "select engine_family || '|' || mode || '|' || (supports ->> 'retry') || '|' || array_to_string(platforms, ',') from public.adapter_capability")).ShouldBe("playwright|full|true|linux-arm64,linux-x64");
    }

    [Fact]
    public async Task ConcurrentIdenticalRegistrations_TwoScopesOneRow()
    {
        await using var world = await StartAsync();
        var results = await Task.WhenAll(
            RegisterAsync(world, Document().ToJsonString()),
            RegisterAsync(world, Document().ToJsonString()));
        results.ShouldAllBe(result => result.IsSuccess);
        results[0].Value.Id.ShouldBe(results[1].Value.Id);
        results.Count(result => result.Value.Created).ShouldBe(1);
        (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability")).ShouldBe(1);
    }

    [Fact]
    public async Task SameNaturalKeyInTwoTenants_TwoIsolatedRows()
    {
        await using var world = await StartAsync();
        var other = await CreateOrgAsync(world.Client, world.User, "other");
        var a = await RegisterAsync(world, Document().ToJsonString());
        var b = await RegisterAsync(world, Document().ToJsonString(), other);
        a.IsSuccess.ShouldBeTrue(a.Error?.Message);
        b.IsSuccess.ShouldBeTrue(b.Error?.Message);
        a.Value.Id.ShouldNotBe(b.Value.Id);
        (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability")).ShouldBe(2);
        foreach (var org in new[] { world.Org, other })
        {
            await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), org);
            (await scope.ScalarAsync<long>("select count(*) from public.adapter_capability")).ShouldBe(1);
        }
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\u0000")]
    public async Task UnstorableEngineText_DirectAndFirstSendReturn422(string escape)
    {
        await using var world = await StartAsync();
        var document = Document().ToJsonString().Replace("playwright", "private-marker" + escape, StringComparison.Ordinal);
        var direct = await RegisterAsync(world, document);
        Invalid(direct.Error);
        var first = await FirstSendAsync(world, Carrier("playwright/1", document));
        Invalid(first.Error);
        JsonSerializer.Serialize(direct.Error).ShouldNotContain("private-marker");
        JsonSerializer.Serialize(first.Error).ShouldNotContain("private-marker");
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\u0000")]
    public async Task UnstorableSchemaText_Returns422NotException(string escape)
    {
        await using var world = await StartAsync();
        var envelope = Carrier("playwright/1", Document().ToJsonString()).Replace("playwright/1", "playwright" + escape + "/1", StringComparison.Ordinal);
        Invalid((await FirstSendAsync(world, envelope)).Error);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task AdapterNameBoundary_FirstSendEnforced(int length, bool accepted)
    {
        await using var world = await StartAsync();
        var result = await FirstSendAsync(world, Carrier(new string('a', length) + "/1", Document().ToJsonString()));
        result.IsSuccess.ShouldBe(accepted);
        if (!accepted) Invalid(result.Error);
    }

    [Fact]
    public async Task OversizeEnvelope_Returns422BeforeRegistration()
    {
        await using var world = await StartAsync();
        var envelope = Carrier("playwright/1", Document().ToJsonString()) + new string(' ', 1_048_576);
        var result = await FirstSendAsync(world, envelope);
        Invalid(result.Error);
        result.Error!.Details!.ShouldContain(detail => detail.Reason == "body_too_large");
        (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability")).ShouldBe(0);
    }

    [Fact]
    public async Task FirstSendCapabilityOver16KiB_Returns422BeforeStorage()
    {
        await using var world = await StartAsync();
        var document = Document();
        document["supports"]![new string('a', 17_000)] = true;
        var result = await FirstSendAsync(world, Carrier("playwright/1", document.ToJsonString()));
        Invalid(result.Error);
        result.Error!.Details!.ShouldContain(detail => detail.Reason == "body_too_large");
        (await fixture.ScalarAsync<long>(world.Database, "select count(*) from public.adapter_capability")).ShouldBe(0);
    }
}
