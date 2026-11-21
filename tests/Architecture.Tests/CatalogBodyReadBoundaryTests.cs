using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every raw-body Catalog route goes through <c>CatalogRequestRules.ReadAuthenticatedJsonAsync</c>: authentication,
/// the size cap and unstorable-text refusal only hold when the body is read there and nowhere else. A route that
/// parsed the body itself would skip the refusal and could answer 500 on a name it cannot read, so a second reader
/// is a boundary break, not a style choice.
/// </summary>
/// <remarks>
/// A source scan, like the healing-switch boundary: the needles are the BCL calls a direct read would use, so a second
/// reader in an endpoint file trips the scan even though it compiles. The rules file is exempt because it is the one
/// reader; the guard and call-site halves pin that it is still used and still classifies the parsed body.
/// </remarks>
public sealed class CatalogBodyReadBoundaryTests
{
    private const string ModuleDirectory = "src/Modules/Evidgate.Modules.Catalog";
    private const string EndpointsDirectory = ModuleDirectory + "/Endpoints";
    private const string RulesFile = EndpointsDirectory + "/CatalogRequestRules.cs";
    private const string SyntheticRouteFile = EndpointsDirectory + "/SyntheticRoute.cs";

    /// <summary>
    /// The eight raw-body routes by handler, per file (suite revision with its dry run, requirement ref, case update,
    /// fixture, fixture revision, git binding, draft create and draft update). Each reads through the shared guard; a
    /// route added here must too. The two draft reads take no body, so they are absent.
    /// </summary>
    private static readonly Dictionary<string, string[]> RawBodyRoutes = new()
    {
        [EndpointsDirectory + "/CatalogEndpoints.cs"] = ["SetCaseRequirementRefAsync", "UpdateCaseAsync", "CreateSuiteRevisionAsync"],
        [EndpointsDirectory + "/FixtureEndpoints.cs"] = ["CreateFixtureAsync", "CreateFixtureRevisionAsync"],
        [EndpointsDirectory + "/GitRepoBindingEndpoints.cs"] = ["RegisterAsync"],
        [EndpointsDirectory + "/DraftEndpoints.cs"] = ["CreateDraftAsync", "UpdateDraftAsync"],
    };

    /// <summary>
    /// Write routes that bind a typed body before any filter runs and so cannot use the raw-body guard. They are known
    /// and NOT guarded: the carried row "Typed-body write routes bind before filters" owns closing that gap.
    /// </summary>
    private static readonly Dictionary<string, string[]> TypedBodyRoutes = new()
    {
        [EndpointsDirectory + "/CatalogEndpoints.cs"] = ["CreateSuiteAsync"],
    };

    /// <summary>
    /// Files outside the shared reader that parse a document that is not the request body (a stored JSON column, a
    /// repository sidecar file, an outbound response). Pinned by matched call text and count, so an added or changed
    /// body-shaped call in one of them is flagged while an unrelated edit is not.
    /// </summary>
    private static readonly Dictionary<string, (string[] Matches, string Reason)> NonRequestParses = new()
    {
        [ModuleDirectory + "/Application/CaseIdentityService.cs"] =
            (["JsonSerializer.Deserialize<JsonElement>( x1"], "decodes the stored case identity metadata column"),
        [ModuleDirectory + "/Application/SuiteRevisionPlanner.cs"] =
            (["JsonSerializer.Deserialize<JsonElement>( x3"], "builds default shard, retry and environment JSON from literals"),
        [ModuleDirectory + "/Application/Queries/SuiteQueries.cs"] =
            (["JsonSerializer.Deserialize<JsonElement>( x3"], "decodes stored suite revision JSON columns for the read model"),
        [ModuleDirectory + "/Application/Commands/SuiteCommands.cs"] =
            (["JsonSerializer.Deserialize<JsonElement>( x1"], "decodes the stored variant params JSON"),
        [ModuleDirectory + "/Infrastructure/GitHub/GitHubClient.cs"] =
            (["ReadJsonAsync( x1"], "reads the outbound GitHub API response through its own capped reader, not a request body"),
        [ModuleDirectory + "/Domain/SidecarManifestReader.cs"] =
            (["JsonDocument.Parse x1"], "parses a repository sidecar manifest after converting it from YAML"),
        [ModuleDirectory + "/Infrastructure/Persistence/DraftReadSql.cs"] =
            (["JsonDocument.Parse x1"], "decodes the stored draft provenance JSON column"),
        [ModuleDirectory + "/Infrastructure/Persistence/TemplateReadSql.cs"] =
            (["JsonDocument.Parse x1"], "decodes the stored template conformance rules JSON column"),
    };

    // Any member named Body that is not a command or query DTO, the synchronous form, or a request alias.
    private static readonly Regex RequestBodyMember = new(
        @"\b(?!(?:command|query)\.Body\b)\w+\.Body\b|\bRequest\.Form\b|=\s*(?:context|httpContext|http)\.Request\s*;",
        RegexOptions.CultureInvariant);

    private static readonly Regex WriteRoute = new(@"\.Map(?!Group\b|Get\b)\w*\(\s*(?:""[^""]*""|[\w.]+)\s*,\s*(\(|\w+)", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> RawHandlerParameterTypes = ["Guid", "HttpContext", "IRequestPrincipalAccessor", "IMediator", "CancellationToken"];

    /// <summary>The calls a route would use to read a request body without the shared cap and refusal.</summary>
    private static readonly string[] DirectBodyReadNeedles =
    [
        "JsonDocument.Parse",
        "ReadFromJsonAsync",
        "JsonNode.Parse",
        "Utf8JsonReader",
        "BodyReader",
        "PipeReader",
        "StreamReader(",
        ".Body.Read",
        ".Body.CopyTo",
        "new StreamReader(",
        "ReadFormAsync",
        "ReadToEnd(",
        "ReadToEndAsync",
        "JsonSerializer.Deserialize<JsonElement>(",
        "JsonSerializer.DeserializeAsync",
        "ReadJsonAsync(",
    ];

    [Fact]
    public void CatalogModule_RawBodyReads_UseSharedBoundary()
    {
        var files = Module();

        files.Length.ShouldBeGreaterThanOrEqualTo(30);
        files.Count(file => file.Path == RulesFile).ShouldBe(1);
        foreach (var folder in new[] { "Endpoints", "Application", "Domain", "Infrastructure", "Contracts" })
        {
            files.ShouldContain(file => file.Path.StartsWith($"{ModuleDirectory}/{folder}/", StringComparison.Ordinal), $"the scan must cover {folder}");
        }

        Scan(files).ShouldBeEmpty("the Catalog module must read request bodies only through CatalogRequestRules.ReadAuthenticatedJsonAsync");
    }

    [Fact]
    public void NonRequestParses_AreStillPresentWithTheirPinnedCalls()
    {
        var files = Module().ToDictionary(file => file.Path, file => file.Text);

        foreach (var (path, pinned) in NonRequestParses)
        {
            files.ContainsKey(path).ShouldBeTrue($"{path} is allow-listed ({pinned.Reason}) but no longer exists: delete the entry");
            Matches(files[path]).ShouldBe(pinned.Matches, $"{path} is allow-listed ({pinned.Reason}); a changed call must be re-judged");
        }
    }

    [Fact]
    public void CatalogWriteRoutes_AreExactlyTheNamedRawBodyAndTypedBodyRoutes()
    {
        var endpoints = Endpoints().Where(file => file.Path != RulesFile).ToArray();
        var expected = RawBodyRoutes.Concat(TypedBodyRoutes)
            .SelectMany(entry => entry.Value.Select(handler => $"{entry.Key}:{handler}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = endpoints
            .SelectMany(file => WriteHandlers(file.Text).Select(handler => $"{file.Path}:{handler}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(expected, "a new Catalog write route must be added to the raw-body or typed-body list, and a raw one must use the guard");
    }

    [Fact]
    public void CatalogRawBodyRoutes_EachCallTheGuardOnceAndNoOtherRouteDoes()
    {
        var endpoints = Endpoints().Where(file => file.Path != RulesFile).ToDictionary(file => file.Path, file => file.Text);

        foreach (var (path, handlers) in RawBodyRoutes)
        {
            foreach (var handler in handlers)
            {
                Occurrences(HandlerBody(endpoints[path], handler), "ReadAuthenticatedJsonAsync(").ShouldBe(1, $"{handler} must read its body through the guard exactly once");
            }
        }

        foreach (var (path, handlers) in TypedBodyRoutes)
        {
            foreach (var handler in handlers)
            {
                Occurrences(HandlerBody(endpoints[path], handler), "ReadAuthenticatedJsonAsync(").ShouldBe(0, $"{handler} binds a typed body and is not behind the raw-body guard");
            }
        }

        endpoints.Values.Sum(text => Occurrences(text, "ReadAuthenticatedJsonAsync("))
            .ShouldBe(RawBodyRoutes.Sum(entry => entry.Value.Length), "a guard call outside a named raw-body route is unaccounted for");

        // Only the requirement-ref route may let a bad value through the reader to report its own field path.
        Occurrences(endpoints[EndpointsDirectory + "/CatalogEndpoints.cs"], "refuseUnstorableValues: false").ShouldBe(1);
        Occurrences(HandlerBody(endpoints[EndpointsDirectory + "/CatalogEndpoints.cs"], "SetCaseRequirementRefAsync"), "refuseUnstorableValues: false").ShouldBe(1);
        endpoints.Where(entry => entry.Key != EndpointsDirectory + "/CatalogEndpoints.cs")
            .Sum(entry => Occurrences(entry.Value, "refuseUnstorableValues")).ShouldBe(0);

        // A raw-body handler takes no typed body, form or attribute-bound parameter: only these injected types.
        foreach (var (path, handlers) in RawBodyRoutes)
        {
            foreach (var handler in handlers)
            {
                HandlerParameterTypes(endpoints[path], handler)
                    .Where(type => !RawHandlerParameterTypes.Contains(type) && type != "GitRepoBindingRegistration")
                    .ShouldBeEmpty($"{handler} must not bind a body parameter");
            }
        }
    }

    [Theory]
    [InlineData("var r = context.Request;")]
    [InlineData("var form = context.Request.Form;")]
    [InlineData("var s = http.Body;")]
    public void Scan_AliasedOrSynchronousRequestAccess_FlagsFile(string source)
    {
        var path = ModuleDirectory + "/Application/Commands/SyntheticHandler.cs";

        Scan([(path, source)]).ShouldBe([path]);
    }

    [Theory]
    [InlineData("group.MapPost(\"/x\", (Foo body) => Ok(body))", "(")]
    [InlineData("group.MapDelete(\"/x\", RemoveAsync)", "RemoveAsync")]
    [InlineData("group.MapPut(\"/x\", ReplaceAsync)", "ReplaceAsync")]
    public void WriteHandlers_FindEveryNonGetRouteIncludingLambdas(string source, string handler) =>
        WriteHandlers(source).ShouldBe([handler]);

    [Theory]
    [InlineData("command.Body, cancellationToken")]
    [InlineData("RuleFor(command => command.Body.SeedVersion)")]
    public void Scan_DomainBodyProperty_IsNotARequestBodyRead(string source) =>
        Scan([(ModuleDirectory + "/Application/Commands/SyntheticHandler.cs", source)]).ShouldBeEmpty();

    [Theory]
    [InlineData("var body = request.Body;")]
    [InlineData("JsonDocument.Parse(text)")]
    [InlineData("await JsonSerializer.DeserializeAsync<JsonElement>(stream)")]
    public void Scan_BodyReadInApplicationFolder_FlagsFile(string source)
    {
        var path = ModuleDirectory + "/Application/Commands/SyntheticHandler.cs";

        Scan([(path, source)]).ShouldBe([path]);
    }

    [Fact]
    public void Scan_AllowListedFileWithAnExtraOrChangedCall_IsFlagged()
    {
        var (path, pinned) = NonRequestParses.First(entry => entry.Value.Matches.Single().StartsWith("JsonDocument.Parse", StringComparison.Ordinal));

        Scan([(path, "JsonDocument.Parse(a); ")]).ShouldBeEmpty();
        Scan([(path, "JsonDocument.Parse(a); JsonDocument.Parse(b);")]).ShouldBe([path]);
        Scan([(path, "JsonDocument.Parse(a); new Utf8JsonReader(bytes);")]).ShouldBe([path]);
        pinned.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ReadJsonAsync_ParsedBody_IsClassifiedBeforeItIsReturned()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, RulesFile));
        var start = text.IndexOf(" Task<(JsonElement Body, Error? Error)> ReadJsonAsync(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "CatalogRequestRules must still declare ReadJsonAsync");
        var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        var reader = text[start..end];
        reader.ShouldContain("UnstorableJsonText.HasUnstorableText(");
        reader.ShouldContain("UnstorableJsonText.HasUnstorableName(");
    }

    [Fact]
    public void ReadAuthenticatedJsonAsync_MissingPrincipal_IsRefusedBeforeReading()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, RulesFile));
        var start = text.IndexOf(" ReadAuthenticatedJsonAsync(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "CatalogRequestRules must declare ReadAuthenticatedJsonAsync");
        var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        var reader = text[start..end];
        var guard = reader.IndexOf("if (principals.Current is null)", StringComparison.Ordinal);
        guard.ShouldBeGreaterThanOrEqualTo(0);
        var refusal = reader.IndexOf("throw new TenantContextMissingException()", guard, StringComparison.Ordinal);
        refusal.ShouldBeGreaterThan(guard);
        var tenantGuard = reader.IndexOf("RawBodyGuard.RequireResolvedTenant(context.RequestServices)", StringComparison.Ordinal);
        tenantGuard.ShouldBeGreaterThan(refusal);
        reader.Contains("IRequestTenantContextAccessor", StringComparison.Ordinal).ShouldBeFalse("the tenant check is the shared guard's, not a local copy");
        var read = reader.IndexOf("ReadJsonAsync(", StringComparison.Ordinal);
        read.ShouldBeGreaterThan(refusal);
        read.ShouldBeGreaterThan(tenantGuard);
    }

    /// <summary>
    /// The mutant proof: a file calling the shared reader is clean and a file reading the body itself is flagged, so a
    /// green <see cref="CatalogModule_RawBodyReads_UseSharedBoundary"/> cannot come from a scan that matches nothing.
    /// </summary>
    [Theory]
    [InlineData("JsonDocument.Parse(requestText)")]
    [InlineData("JsonDocument.ParseAsync(context.Request.Body)")]
    [InlineData("context.Request.ReadFromJsonAsync<JsonElement>()")]
    [InlineData("context.Request.Body.ReadAsync(buffer)")]
    [InlineData("context.Request.BodyReader.ReadAsync()")]
    [InlineData("request.BodyReader.ReadAsync()")]
    [InlineData("JsonNode.Parse(requestText)")]
    [InlineData("new Utf8JsonReader(bytes)")]
    [InlineData("var body = req.Body;")]
    [InlineData("await req.Body.CopyToAsync(buffer)")]
    [InlineData("await context.Request.ReadFormAsync()")]
    [InlineData("reader.ReadToEnd()")]
    [InlineData("JsonSerializer.DeserializeAsync<JsonElement>(stream)")]
    [InlineData("HttpRequest request; new StreamReader(request.Body)")]
    [InlineData("request.Body.ReadAsync(buffer)")]
    [InlineData("ReadBody(request.Body, cancellationToken)")]
    [InlineData("PipeReader.Create(stream)")]
    [InlineData("reader.ReadToEndAsync()")]
    [InlineData("JsonSerializer.Deserialize<JsonElement>(requestText)")]
    [InlineData("CatalogRequestRules.ReadJsonAsync(context, limit, cancellationToken)")]
    public void Scan_DirectBodyReadOutsideRules_FlagsFile(string source)
    {
        (string Path, string Text) sharedRead =
            (SyntheticRouteFile, "CatalogRequestRules.ReadAuthenticatedJsonAsync(context, principals, limit, cancellationToken)");
        (string Path, string Text) direct = (SyntheticRouteFile, source);

        Scan([sharedRead]).ShouldBeEmpty();
        Scan([direct]).ShouldBe([SyntheticRouteFile]);
    }

    /// <summary>The shared reader owns the raw read and the parse, so those calls are clean inside it.</summary>
    [Theory]
    [InlineData("JsonDocument.Parse(requestText)")]
    [InlineData("JsonNode.Parse(requestText)")]
    [InlineData("new Utf8JsonReader(bytes)")]
    [InlineData("var body = req.Body;")]
    [InlineData("await req.Body.CopyToAsync(buffer)")]
    [InlineData("await context.Request.ReadFormAsync()")]
    [InlineData("reader.ReadToEnd()")]
    [InlineData("JsonSerializer.DeserializeAsync<JsonElement>(stream)")]
    [InlineData("context.Request.ReadFromJsonAsync<JsonElement>()")]
    [InlineData("context.Request.Body.ReadAsync(buffer)")]
    [InlineData("context.Request.BodyReader.ReadAsync()")]
    [InlineData("HttpRequest request; new StreamReader(request.Body)")]
    [InlineData("request.Body.ReadAsync(buffer)")]
    [InlineData("ReadBody(request.Body, cancellationToken)")]
    [InlineData("PipeReader.Create(stream)")]
    [InlineData("reader.ReadToEndAsync()")]
    [InlineData("JsonSerializer.Deserialize<JsonElement>(requestText)")]
    [InlineData("ReadJsonAsync(context, limit, cancellationToken)")]
    public void Scan_DirectBodyReadInsideRules_AllowsFile(string source) =>
        Scan([(RulesFile, source)]).ShouldBeEmpty();

    private static (string Path, string Text)[] Module() => [.. SolutionLayout.SourcesOf(ModuleDirectory)];

    private static (string Path, string Text)[] Endpoints() =>
        Module().Where(file => file.Path.StartsWith(EndpointsDirectory + "/", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Files that read a body-shaped call and are not the shared reader. A file is clean when it has no match, or when
    /// it is allow-listed and its matches equal the pinned call texts and counts exactly.
    /// </summary>
    private static string[] Scan(IEnumerable<(string Path, string Text)> files) =>
        files
            .Where(file => file.Path != RulesFile)
            .Where(file =>
            {
                var matches = Matches(file.Text);
                return matches.Length > 0
                    && !(NonRequestParses.TryGetValue(file.Path, out var pinned) && matches.SequenceEqual(pinned.Matches));
            })
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Each matched call text with its count, sorted, so an unrelated edit does not move a pin.</summary>
    private static string[] Matches(string text) =>
        DirectBodyReadNeedles
            .Select(needle => (Needle: needle, Count: Occurrences(text, needle)))
            .Concat(RequestBodyMember.Matches(text).Select(match => match.Value).GroupBy(value => value)
                .Select(group => (Needle: group.Key, Count: group.Count())))
            .Where(entry => entry.Count > 0)
            .Select(entry => $"{entry.Needle} x{entry.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The write verbs a route file maps, as handler name to route, in declaration order.</summary>
    private static string[] WriteHandlers(string text) =>
        WriteRoute.Matches(text).Select(match => match.Groups[1].Value).ToArray();

    /// <summary>The parameter types of a handler, so a typed body or form parameter shows up as an unknown type.</summary>
    private static string[] HandlerParameterTypes(string text, string handler)
    {
        var declaration = Regex.Match(text, $@"\bTask(?:<.+>)?\s+{handler}\(([^)]*)\)");
        declaration.Success.ShouldBeTrue($"{handler} must be declared as a Task-returning method");

        return declaration.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter[..parameter.LastIndexOf(' ')].Trim())
            .ToArray();
    }

    private static string HandlerBody(string text, string handler)
    {
        var declaration = Regex.Match(text, $@"\bTask(?:<.+>)?\s+{handler}\(");
        declaration.Success.ShouldBeTrue($"{handler} must be declared as a Task-returning method");
        var end = text.IndexOf("\n    }", declaration.Index, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(declaration.Index);

        return text[declaration.Index..end];
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
