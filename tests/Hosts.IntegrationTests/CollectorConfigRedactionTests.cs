using YamlDotNet.RepresentationModel;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// Guards the collector's redaction contract, which the application cannot enforce for an attribute
/// name a caller supplied itself: every pipeline drops the secret-shaped attributes <em>before</em> it
/// batches (a processor after the export stage is invisible to the backend), and every receiver
/// listens on all interfaces so only the compose port mapping decides what the host exposes.
/// </summary>
/// <remarks>
/// These are structural guards over the configuration text, not proof that redaction happens: they read
/// which statements exist, where the pipeline orders them, and what they name, so they catch removal,
/// reordering, a pattern narrowed to <c>^$</c>, a <c>where false</c> guard and a statement trimmed to a
/// subset of the contracted key names — but a regex that compiles and matches nothing still reads green.
/// Whether a secret key actually disappears, and a body or attribute value is actually masked, is only
/// settled by driving the collector and reading the store back (the observability smoke), plus the
/// security review that owns this control.
/// Both stock components are accepted by name (<c>redaction</c> or <c>transform</c>): the stock
/// <c>redaction</c> processor masks a matched key with <c>****</c> and ships its name anyway, so a
/// pipeline that satisfies the name check but carries no deleting statement still fails here. Removing
/// redaction from one pipeline fails even when the other two keep theirs, because the guard runs per
/// signal.
/// </remarks>
public sealed class CollectorConfigRedactionTests
{
    private const string ConfigRelativePath = "infra/observability/otel-collector.yaml";

    /// <summary>
    /// Per signal, the attribute maps the contract names. A map left out keeps its secret-shaped keys,
    /// so each one is asserted separately rather than through a whole-processor keyword search.
    /// </summary>
    private static readonly Dictionary<string, string[]> Contexts = new(StringComparer.Ordinal)
    {
        ["logs"] = ["resource", "scope", "log"],
        ["metrics"] = ["resource", "scope", "datapoint"],
        ["traces"] = ["resource", "scope", "span", "spanevent"],
    };

    /// <summary>The pipelines the application exports to, named as the collector names them.</summary>
    private static readonly string[] Signals = [.. Contexts.Keys];

    [Fact]
    public void Every_Pipeline_Redacts_Before_It_Batches()
    {
        var pipelines = Pipelines(ReadConfig());

        foreach (var signal in Signals)
        {
            var processors = RequiredPipeline(pipelines, signal);
            var redaction = processors.FindIndex(IsRedactionProcessor);
            var batch = processors.IndexOf("batch");
            var list = string.Join(", ", processors);

            Assert.True(redaction >= 0, $"{signal} pipeline has no redaction processor: [{list}]");
            Assert.True(batch >= 0, $"{signal} pipeline has no batch processor: [{list}]");
            Assert.True(redaction < batch, $"{signal} pipeline batches before it redacts: [{list}]");
        }
    }

    [Fact]
    public void Every_Attribute_Context_Deletes_Secret_Shaped_Attribute_Keys()
    {
        var root = ReadConfig();
        var pipelines = Pipelines(root);

        foreach (var (signal, contexts) in Contexts)
        {
            var definitions = RedactionProcessors(root, pipelines, signal);

            foreach (var context in contexts)
            {
                // A masked key still tells a reader that the value existed, and the app redactor cannot
                // see an attribute it never wrote, so the deletion is the part only the collector can do
                // — in every map the contract names, because an untargeted context ships its keys.
                var statements = definitions
                    .SelectMany(definition => ContextStatements(definition, signal, context))
                    .ToArray();

                Assert.True(
                    statements.Any(DeletesSecretShapedKeys),
                    $"{signal} redaction has no statement that structurally deletes secret-shaped keys "
                        + $"in the '{context}' attribute map");
            }
        }
    }

    [Fact]
    public void The_Log_Pipeline_Masks_Secret_Shaped_Values()
    {
        var root = ReadConfig();
        var statements = RedactionStatements(root, Pipelines(root), "logs");

        // The log body carries assignments and header pairs the app redactor never parses; both the
        // body and the string attribute values are contracted to end up masked to `***`.
        Assert.True(
            statements.Any(statement => MasksSecretShapedValues(statement, "body")),
            "the logs pipeline has no statement that structurally masks secret-shaped assignments in the log body");
        Assert.True(
            statements.Any(statement => MasksSecretShapedValues(statement, "attributes")),
            "the logs pipeline has no statement that structurally masks secret-shaped values in log attributes");
    }

    [Fact]
    public void Every_Receiver_Endpoint_Listens_On_All_Interfaces()
    {
        var receivers = Required(Mapping(ReadConfig(), "receivers"), "receivers");

        // The application exports OTLP to this collector, so that receiver is the seam that has to exist.
        Required(Mapping(receivers, "otlp"), "receivers.otlp");

        var endpoints = EndpointValues(receivers).ToArray();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            Assert.True(
                endpoint.StartsWith("0.0.0.0:", StringComparison.Ordinal),
                $"a receiver binds '{endpoint}': inside the compose network the collector has to listen "
                    + "on every interface, and the published port decides the exposure");
        }
    }

    private static YamlMappingNode ReadConfig()
    {
        var path = Path.Combine(HostLocator.RepoRoot, ConfigRelativePath);
        Assert.True(File.Exists(path), $"collector configuration not found at {path}");

        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(path)));
        Assert.Single(yaml.Documents);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    /// <summary>Pipeline name to its processor names, in the order the collector applies them.</summary>
    private static Dictionary<string, List<string>> Pipelines(YamlMappingNode root)
    {
        var service = Required(Mapping(root, "service"), "service");
        var pipelines = Required(Mapping(service, "pipelines"), "service.pipelines");

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, node) in Children(pipelines))
        {
            result[name] = ProcessorNames(Child(node, "processors"));
        }

        return result;
    }

    private static List<string> RequiredPipeline(Dictionary<string, List<string>> pipelines, string signal) =>
        pipelines.TryGetValue(signal, out var processors)
            ? processors
            : throw new InvalidOperationException($"{ConfigRelativePath} has no '{signal}' pipeline");

    /// <summary>
    /// The key names the contract requires deleted. Requiring all of them is the point: a statement
    /// trimmed to <c>token|secret</c> would still delete something, and still ship a password.
    /// </summary>
    private static readonly string[] SecretKeyWords =
    [
        "authorization",
        "cookie",
        "token",
        "secret",
        "password",
        "passwd",
        "pwd",
        "api",
        "credential",
        "pepper",
        "__host-",
        "__secure-",
    ];

    /// <summary>
    /// Every scalar under the redaction processors of one pipeline. Aliases arrive resolved: the loader
    /// shares one node for an anchored statement and for each <c>*</c> reuse of it, so a statement written
    /// once is still seen once per context group it was applied to.
    /// </summary>
    private static string[] RedactionStatements(
        YamlMappingNode root,
        Dictionary<string, List<string>> pipelines,
        string signal) =>
        [.. RedactionProcessors(root, pipelines, signal).SelectMany(Scalars)];

    /// <summary>Every redaction processor definition a pipeline applies, resolved from the processors section.</summary>
    private static YamlMappingNode[] RedactionProcessors(
        YamlMappingNode root,
        Dictionary<string, List<string>> pipelines,
        string signal)
    {
        var processors = Required(Mapping(root, "processors"), "processors");
        var names = RequiredPipeline(pipelines, signal)
            .Where(IsRedactionProcessor)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(names.Length > 0, $"{signal} pipeline has no redaction processor to inspect");

        return [.. names.Select(name => Required(Mapping(processors, name), $"processors.{name}"))];
    }

    /// <summary>
    /// The statements a definition applies to one context group. The alias the file uses to write the key
    /// pattern once arrives resolved, so a reused statement is seen in every group that applies it, and a
    /// group dropped from the definition is seen by nobody.
    /// </summary>
    private static IEnumerable<string> ContextStatements(
        YamlMappingNode definition,
        string signal,
        string context)
    {
        if (Child(definition, StatementSection(signal)) is not YamlSequenceNode groups)
        {
            return [];
        }

        return groups
            .OfType<YamlMappingNode>()
            .Where(group => Scalar(Child(group, "context")) == context)
            .SelectMany(group => Scalars(Child(group, "statements") ?? group));
    }

    /// <summary>The section a signal's transform processor declares its context groups under.</summary>
    private static string StatementSection(string signal) => signal switch
    {
        "logs" => "log_statements",
        "metrics" => "metric_statements",
        _ => "trace_statements",
    };

    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    /// <summary>
    /// Structural check of a statement's text, not proof that it redacts: a pattern of <c>^$</c> or a
    /// trailing <c>where false</c> matches nothing, so both are excluded here, and whether a real secret
    /// key disappears is only settled by a collector ingest-then-readback.
    /// </summary>
    private static bool DeletesSecretShapedKeys(string statement) =>
        statement.Contains("delete_matching_keys(", StringComparison.Ordinal)
        && !statement.Contains("where false", StringComparison.Ordinal)
        && !statement.Contains("\"^$\"", StringComparison.Ordinal)
        && SecretKeyWords.All(word => statement.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A replacement that structurally masks <paramref name="field"/> to the contracted <c>***</c>.</summary>
    private static bool MasksSecretShapedValues(string statement, string field) =>
        statement.Contains("replace_", StringComparison.Ordinal)
        && statement.Contains(field, StringComparison.Ordinal)
        && statement.Contains("***", StringComparison.Ordinal)
        && !statement.Contains("where false", StringComparison.Ordinal)
        && SecretKeyWords.Any(word => statement.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every scalar value below <paramref name="node"/>: mapping keys are names, not content.</summary>
    private static IEnumerable<string> Scalars(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode { Value: { } value }:
                yield return value;
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence)
                {
                    foreach (var nested in Scalars(child))
                    {
                        yield return nested;
                    }
                }

                break;

            case YamlMappingNode mapping:
                foreach (var (_, child) in Children(mapping))
                {
                    foreach (var nested in Scalars(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static YamlMappingNode Required(YamlMappingNode? node, string section) =>
        node ?? throw new InvalidOperationException($"{ConfigRelativePath} has no '{section}' mapping");

    /// <summary>
    /// Either stock component, by the name a deployment gave it: <c>transform</c> deletes attributes
    /// through its statements, and <c>redaction</c> is accepted here because only the statement guard
    /// below can tell whether it actually deletes.
    /// </summary>
    private static bool IsRedactionProcessor(string processor) =>
        processor.StartsWith("redaction", StringComparison.Ordinal)
        || processor.StartsWith("transform", StringComparison.Ordinal);

    private static List<string> ProcessorNames(YamlNode? node) =>
        node is YamlSequenceNode sequence
            ? [.. sequence.Children.Select(child => (child as YamlScalarNode)?.Value ?? string.Empty)]
            : [];

    /// <summary>Every <c>endpoint</c> value at any depth below <paramref name="node"/>.</summary>
    private static IEnumerable<string> EndpointValues(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var pair in mapping)
                {
                    if (pair.Key is YamlScalarNode { Value: "endpoint" }
                        && pair.Value is YamlScalarNode { Value: { } endpoint })
                    {
                        yield return endpoint;
                    }

                    foreach (var nested in EndpointValues(pair.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence)
                {
                    foreach (var nested in EndpointValues(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static IEnumerable<(string Name, YamlNode Node)> Children(YamlMappingNode mapping)
    {
        foreach (var pair in mapping)
        {
            if (pair.Key is YamlScalarNode { Value: { } name })
            {
                yield return (name, pair.Value);
            }
        }
    }

    private static YamlMappingNode? Mapping(YamlNode? node, string key) => Child(node, key) as YamlMappingNode;

    private static YamlNode? Child(YamlNode? node, string key)
    {
        if (node is not YamlMappingNode mapping)
        {
            return null;
        }

        foreach (var pair in mapping)
        {
            if (pair.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
