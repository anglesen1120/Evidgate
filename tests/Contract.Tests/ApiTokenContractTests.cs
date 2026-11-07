using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The API-token wire contract: the three operations a generated client binds, the one-time secret the create answer
/// carries, and the metadata-only list view.
/// </summary>
/// <remarks>
/// Shapes and parameters are parsed from <c>contracts/openapi.yaml</c> through <see cref="OpenApiSpec"/> with component
/// references resolved, never restated here. The one exception is the refusal vocabulary: those codes have no
/// machine-readable registry, so they are asserted as the documentation a client reads, not as proof a handler
/// produces them (the module suite owns that).
/// </remarks>
public sealed class ApiTokenContractTests
{
    private const string CollectionPath = "/projects/{p}/api-tokens";
    private const string RevokePath = "/api-tokens/{t}";

    /// <summary>The listed token's eight wire fields: metadata only, never the secret or its HMAC.</summary>
    private static readonly string[] ViewFields =
    [
        "id", "name", "prefix", "permissions", "status", "expires_at", "last_used_at", "created_by",
    ];

    /// <summary>The create answer's six wire fields, including the one-time <c>token</c>.</summary>
    private static readonly string[] CreatedFields =
    [
        "id", "name", "prefix", "token", "permissions", "expires_at",
    ];

    [Fact]
    public void Spec_Declares_TheThreeApiTokenOperations()
    {
        var spec = OpenApiSpec.Load();

        spec.PathKeys.ShouldContain(CollectionPath);
        spec.PathKeys.ShouldContain(RevokePath);
        spec.SchemaNames.ShouldContain("CreateApiTokenRequest");
        spec.SchemaNames.ShouldContain("ApiTokenCreatedResponse");
        spec.SchemaNames.ShouldContain("ApiTokenListResponse");
        spec.SchemaNames.ShouldContain("ApiTokenView");
    }

    /// <summary>
    /// The create operation's status set, and the refusal vocabulary it publishes: an escalation is a 403, a deny-set
    /// code and an unusable expiry are 422s. The codes are documentation — no registry or producer is reachable from
    /// this suite — so this pins what a client is told, beside the statuses the operation actually declares.
    /// </summary>
    [Fact]
    public void Create_Operation_DeclaresItsRefusals()
    {
        var spec = OpenApiSpec.Load();

        spec.ResponseKeys(CollectionPath, "post").ShouldBe(
            ["200", "401", "403", "404", "413", "422", "429", "503"]);

        var documented = spec.DocumentedText("createApiToken");
        documented.ShouldContain(
            "403 `permission_escalation`",
            Case.Sensitive,
            "a permission the creator does not hold is refused as a 403 escalation");
        documented.ShouldContain(
            "422 `forbidden_token_permission`",
            Case.Sensitive,
            "a deny-set code is refused as a 422");
        documented.ShouldContain("`schema_invalid`", Case.Sensitive, "an unusable expiry is a 422 schema_invalid");
    }

    /// <summary>
    /// The create takes no <c>Idempotency-Key</c>: its answer holds a secret that cannot be stored for replay. The
    /// parameters are read with every component reference resolved, and the control is the neighbouring ingest-token
    /// revoke, whose shared header the same reader finds — so the absence is a fact about this route.
    /// </summary>
    [Fact]
    public void Create_Operation_TakesNoIdempotencyKey()
    {
        var spec = OpenApiSpec.Load();
        var headers = ParameterNames(spec, CollectionPath, "post", "header");

        headers.ShouldContain("X-XSRF-TOKEN", "the shared header the operation references must resolve");
        headers.ShouldNotContain("Idempotency-Key", "a one-time secret must never be replayed from a key");
        ParameterNames(spec, CollectionPath, "post", "path").ShouldBe(["p"]);

        ParameterNames(spec, "/ingest-tokens/{t}", "delete", "header")
            .ShouldContain("Idempotency-Key", "the control route declares the header the API-token routes omit");
    }

    [Fact]
    public void List_Operation_ReturnsTheMetadataPage()
    {
        var spec = OpenApiSpec.Load();

        spec.ResponseKeys(CollectionPath, "get").ShouldBe(["200", "401", "403", "404", "422", "429", "503"]);
        ParameterNames(spec, CollectionPath, "get", "query").ShouldBe(["cursor", "limit"]);

        var list = spec.Schema("ApiTokenListResponse");
        Assert.Equal(["items", "next_cursor"], Required(list));

        var rows = list["properties"]?["items"]?["items"];
        if (rows?["$ref"] is not JsonValue reference)
        {
            throw new InvalidOperationException("ApiTokenListResponse.items.items must name the row schema");
        }

        reference.GetValue<string>().ShouldBe(
            "#/components/schemas/ApiTokenView",
            "the page's rows must be the metadata view, not the create answer that carries the secret");
    }

    /// <summary>
    /// Revocation answers one bodiless success, the 404 of an id the tenant cannot see, or the 412 a losing
    /// writer of the same row gets — the handler maps a lost concurrency check to <c>precondition_failed</c>.
    /// </summary>
    [Fact]
    public void Revoke_Operation_AnswersNoContentNotFoundOrConcurrencyRefusal()
    {
        var spec = OpenApiSpec.Load();

        spec.ResponseKeys(RevokePath, "delete").ShouldBe(
            ["204", "401", "403", "404", "412", "429", "503"],
            "revocation answers one bodiless success (204), the 404 of an id this tenant cannot see, "
            + "or the 412 of a revocation that lost the row to a concurrent writer");

        spec.DocumentedText("revokeApiToken").ShouldContain(
            "412 `precondition_failed`",
            Case.Sensitive,
            "a client reading the operation must be told the concurrency refusal it can receive");

        ParameterNames(spec, RevokePath, "delete", "path").ShouldBe(["t"]);
        ParameterNames(spec, RevokePath, "delete", "header")
            .ShouldNotContain("Idempotency-Key", "a repeated revoke answers the same 204 without a key");
    }

    [Fact]
    public void ApiTokenView_Schema_IsMetadataOnly()
    {
        var view = OpenApiSpec.Load().Schema("ApiTokenView");
        var properties = Properties(view);

        Assert.Equal(Sorted(ViewFields), Sorted(properties));
        Assert.Equal(Sorted(ViewFields), Sorted(Required(view)));

        foreach (var secret in new[] { "token", "token_hash", "secret", "hmac" })
        {
            Assert.DoesNotContain(secret, properties);
        }
    }

    [Fact]
    public void Created_Response_Schema_RequiresTheOneTimeToken()
    {
        var spec = OpenApiSpec.Load();
        var created = spec.Schema("ApiTokenCreatedResponse");

        Assert.Equal(Sorted(CreatedFields), Sorted(Properties(created)));
        Assert.Equal(Sorted(CreatedFields), Sorted(Required(created)));
        Assert.Contains("token", Required(created));

        var request = spec.Schema("CreateApiTokenRequest");
        Assert.Equal(["name", "permissions", "expires_at"], Required(request));
        Assert.Equal(Sorted(["name", "permissions", "expires_at"]), Sorted(Properties(request)));
    }

    /// <summary>The <c>name</c> values of an operation's parameters in one location, references resolved.</summary>
    /// <param name="spec">The loaded spec.</param>
    /// <param name="pathKey">The spec path key.</param>
    /// <param name="method">The lower-case HTTP method.</param>
    /// <param name="location">The <c>in</c> value, e.g. <c>header</c>.</param>
    private static string[] ParameterNames(OpenApiSpec spec, string pathKey, string method, string location) =>
    [
        .. spec.OperationParameters(pathKey, method, location)
            .Select(parameter => parameter["name"] is JsonValue name
                ? name.GetValue<string>()
                : throw new InvalidOperationException($"{method} {pathKey} declares a parameter with no name")),
    ];

    /// <summary>The property names a parsed schema declares, in document order.</summary>
    private static string[] Properties(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            throw new InvalidOperationException("the schema declares no properties mapping");
        }

        return [.. properties.Select(member => member.Key)];
    }

    /// <summary>The names a parsed schema's <c>required</c> list declares, in document order.</summary>
    private static string[] Required(JsonObject schema)
    {
        if (schema["required"] is not JsonArray required)
        {
            throw new InvalidOperationException("the schema declares no required list");
        }

        return
        [
            .. required.Select(name => name is JsonValue value
                ? value.GetValue<string>()
                : throw new InvalidOperationException("a required entry is not a string")),
        ];
    }

    private static string[] Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];
}
