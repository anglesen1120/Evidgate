using System.Buffers;
using System.Text.Json;
using Corvus.Json;
using Corvus.Json.Validator;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// The compiled <c>contracts/schemas/*.json</c> and <c>contracts/adapter/*.json</c>
/// documents (00 §5.2/§5.5 edge), loaded once at boot and shared by every request
/// (01.3.2-T2.1). The CLI's Ajv module (<c>packages/cli/src/validate.ts</c>) runs
/// the identical algorithm over the same files, and both are exercised against the
/// <c>fixtures/contracts/</c> parity corpus (AC3, T42).
/// </summary>
/// <remarks>
/// A schema is treated as <em>batch-capable</em> when a <c>$defs</c> member holds
/// <c>properties.events.items.$ref</c> pointing at a sibling <c>$defs</c> entry
/// (the 00 §9.4 envelope: <c>$defs.batch → $defs.event</c>). For batch payloads
/// the engine runs the <em>envelope</em> schema — the batch def with item
/// validation removed — and then the <em>item</em> schema per event, which is what
/// gives each per-item error its 0-based <see cref="ContractValidationError.Index"/>.
/// When the envelope itself is invalid, item errors are skipped: 00 §9.4 reports
/// per-event indices only for a well-formed batch.
/// </remarks>
public sealed class ContractSchemaSet
{
    /// <summary>JSON Schema draft all contract schemas declare (00 §5.2).</summary>
    private const string SchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// The <c>contracts/</c> sub-directories holding JSON Schema documents, each
    /// searched recursively (01.3.2-T2, extended by 04.7.1-T1.2/05.1.1-T1.1).
    /// </summary>
    private static readonly string[] SchemaRoots = ["schemas", "adapter", "manifest", "catalog"];

    private readonly IReadOnlyDictionary<string, ContractSchema> _schemas;

    private ContractSchemaSet(IReadOnlyDictionary<string, ContractSchema> schemas) => _schemas = schemas;

    /// <summary>The schema keys that were loaded (file name without <c>.json</c>).</summary>
    public IEnumerable<string> Keys => _schemas.Keys;

    /// <summary>
    /// Loads every <c>*.json</c> schema under <c>&lt;contractsDirectory&gt;/schemas</c>,
    /// <c>/adapter</c> (recursively, so <c>adapter/operations/*.v1.json</c> is
    /// included) and <c>/manifest</c>, keyed by file name without extension
    /// (<c>event-envelope.v1</c>, <c>capability.v1</c>, <c>execute.v1</c>,
    /// <c>manifest.v1</c>). The whole set is registered in one in-memory
    /// document resolver under the canonical URI
    /// <c>urn:evidgate:contract:&lt;key&gt;</c>, which is how a schema addresses
    /// another file — <c>execute.v1</c> references
    /// <c>urn:evidgate:contract:event-envelope.v1#/$defs/event</c> instead of
    /// copying the 00 §9.4 envelope. File-system and HTTP resolution stay off.
    /// </summary>
    /// <param name="contractsDirectory">The repository's <c>contracts/</c> directory.</param>
    /// <exception cref="InvalidOperationException">
    /// No schema file was found, or two files load under the same key.
    /// </exception>
    public static ContractSchemaSet Load(string contractsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractsDirectory);

        var documents = new Dictionary<string, (string Origin, string Text)>(StringComparer.Ordinal);
        foreach (var root in SchemaRoots)
        {
            var directory = Path.Combine(contractsDirectory, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory
                .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                if (documents.TryGetValue(key, out var existing))
                {
                    // Keys are file names without extension and address a schema
                    // from every runtime (and the fixtures/contracts/<key>/
                    // corpus directory), so a duplicate is a contract bug, not a
                    // last-one-wins.
                    throw new InvalidOperationException(
                        $"duplicate contract schema key '{key}': '{existing.Origin}' and '{file}' both load under it");
                }

                documents.Add(key, (file, File.ReadAllText(file)));
            }
        }

        if (documents.Count == 0)
        {
            throw new InvalidOperationException($"no contract schema (*.json) found under '{contractsDirectory}'");
        }

        // One registry for the whole set: every document is reachable by its
        // canonical URI, so a cross-file $ref resolves from memory and never
        // touches the file system or the network.
        var resolver = new PrepopulatedDocumentResolver();
        foreach (var (key, document) in documents)
        {
            resolver.AddDocument(CanonicalUri(key), JsonDocument.Parse(document.Text));
        }

        var options = SchemaOptions(resolver);
        var schemas = new Dictionary<string, ContractSchema>(StringComparer.Ordinal);
        foreach (var (key, document) in documents)
        {
            schemas[key] = ContractSchema.Build(key, document.Text, options);
        }

        return new ContractSchemaSet(schemas);
    }

    /// <summary>
    /// The canonical URI a schema is addressed by, in a document's own
    /// <c>$ref</c> as well as in the resolver: <c>urn:evidgate:contract:&lt;key&gt;</c>.
    /// </summary>
    public static string CanonicalUri(string schemaKey) => $"urn:evidgate:contract:{schemaKey}";

    /// <summary>True when <paramref name="schemaKey"/> was loaded.</summary>
    public bool Contains(string schemaKey) => _schemas.ContainsKey(schemaKey);

    /// <summary>
    /// The schema's <c>x-max-bytes</c> transport cap (00 §9.3 body limits), or
    /// <c>null</c> when the schema declares none. Applies to the encoded body
    /// before parsing.
    /// </summary>
    public long? MaxBytes(string schemaKey) => Get(schemaKey).MaxBytes;

    /// <summary>
    /// Validates a raw UTF-8 JSON body: the <c>x-max-bytes</c> cap, then JSON
    /// well-formedness (<see cref="ContractValidationError.InvalidJsonReason"/>),
    /// then the schema. A byte-capped or unparseable body is never handed to the
    /// schema engine.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown <paramref name="schemaKey"/>.</exception>
    public ContractValidationResult Validate(string schemaKey, ReadOnlyMemory<byte> utf8Json)
    {
        var schema = Get(schemaKey);
        if (schema.MaxBytes is { } maxBytes && utf8Json.Length > maxBytes)
        {
            return new ContractValidationResult([ContractValidationError.BodyTooLarge]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(new ReadOnlySequence<byte>(utf8Json));
        }
        catch (JsonException)
        {
            return new ContractValidationResult([ContractValidationError.InvalidJson]);
        }

        using (document)
        {
            return Validate(schema, document.RootElement);
        }
    }

    /// <summary>
    /// Validates an already-parsed document (the endpoint-filter path: minimal-API
    /// binding produces a <see cref="JsonDocument"/>/<see cref="JsonElement"/>
    /// argument and the filter validates it before the handler materializes DTOs).
    /// The byte cap is not applied here — the filter checks the transport length
    /// itself.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown <paramref name="schemaKey"/>.</exception>
    public ContractValidationResult Validate(string schemaKey, JsonElement document)
        => Validate(Get(schemaKey), document);

    private static ContractValidationResult Validate(ContractSchema schema, JsonElement document)
    {
        var errors = new ErrorCollector();

        // Batch dispatch: an object carrying an 'events' member is the 00 §9.4
        // batch form and validates as envelope + items; anything else validates
        // as a single item (§9.4's single-event example) — or against the whole
        // schema when it has no batch/item pair at all (capability document).
        if (schema.Envelope is { } envelope && schema.Item is { } itemSchema
            && document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty("events", out _))
        {
            Collect(schema, envelope, schema.EnvelopeDocument, document, index: null, prefix: string.Empty, errors);
            if (errors.Count == 0 && document.TryGetProperty("events", out var events)
                && events.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in events.EnumerateArray())
                {
                    Collect(schema, itemSchema, schema.ItemDocument, item, index, $"/events/{index}", errors);
                    index++;
                }
            }

            return errors.Result();
        }

        var singleSchema = schema.Item ?? schema.Root;
        var singleDocument = schema.ItemDocument ?? schema.Document;
        Collect(schema, singleSchema, singleDocument, document, index: null, prefix: string.Empty, errors);
        return errors.Result();
    }

    /// <summary>
    /// Runs <paramref name="schema"/> against <paramref name="instance"/> and maps
    /// each Corvus result to the shared (index, path, reason) shape.
    /// <paramref name="prefix"/> re-anchors item-relative pointers under
    /// <c>/events/&lt;i&gt;</c> for the 00 §9.2 absolute path.
    /// </summary>
    private static void Collect(
        ContractSchema owner,
        JsonSchema schema,
        JsonNode? schemaDocument,
        JsonElement instance,
        int? index,
        string prefix,
        ErrorCollector errors)
    {
        // Flag first: an accepted document skips the Detailed pass entirely
        // (a valid 1 MiB body is 19.2 ms/13.8 MB Detailed vs 8.2 ms/48 B Flag;
        // the verdict is identical — the parity corpus proves it).
        if (schema.Validate(instance, ValidationLevel.Flag).IsValid)
        {
            return;
        }

        var context = schema.Validate(instance, ValidationLevel.Detailed);
        var covered = ContractErrorMapper.CoveredKeywordLocations(context);
        var schemaTree = schemaDocument ?? owner.Document;
        var mapped = 0;
        foreach (var result in context.Results)
        {
            if (result.Valid)
            {
                continue;
            }

            var error = ContractErrorMapper.Map(result, schemaTree, index, prefix, covered);
            if (error is not null)
            {
                mapped++;
                if (!errors.Add(error))
                {
                    // Detail cap fired — the collector emitted its terminal
                    // 'truncated' marker; stop spending on mapping.
                    return;
                }
            }
        }

        if (mapped == 0)
        {
            // A document the engine calls invalid can never surface as valid:
            // even a row that maps to nothing leaves a generic self-leaf trace.
            errors.Add(new ContractValidationError(index, prefix, "invalid"));
        }
    }

    /// <summary>
    /// Ordered row collection + O(1) membership for the mapped
    /// <see cref="ContractValidationError"/> triples: deduplicates on
    /// (index, path, reason) and hard-caps the list at
    /// <see cref="ContractValidationResult.MaxDetails"/> — the moment a further
    /// distinct row would exceed it, the single terminal
    /// <see cref="ContractValidationError.Truncated"/> marker is appended as the
    /// last element and collection stops (the identical rule the CLI applies,
    /// fixtures/contracts/README.md).
    /// </summary>
    private sealed class ErrorCollector
    {
        private readonly List<ContractValidationError> _errors = [];
        private readonly HashSet<ContractValidationError> _seen = new();
        private bool _truncated;

        public int Count => _errors.Count;

        /// <returns>False once the cap has fired and the marker row is in place.</returns>
        public bool Add(ContractValidationError error)
        {
            if (_truncated)
            {
                return false;
            }

            if (!_seen.Add(error))
            {
                return true;
            }

            if (_errors.Count >= ContractValidationResult.MaxDetails)
            {
                _errors.Add(ContractValidationError.Truncated);
                _seen.Add(ContractValidationError.Truncated);
                _truncated = true;
                return false;
            }

            _errors.Add(error);
            return true;
        }

        public ContractValidationResult Result() => new(_errors);
    }

    private ContractSchema Get(string schemaKey) =>
        _schemas.TryGetValue(schemaKey, out var schema)
            ? schema
            : throw new ArgumentException(
                $"unknown contract schema '{schemaKey}' (loaded: {string.Join(", ", _schemas.Keys)})",
                nameof(schemaKey));

    /// <summary>One loaded schema plus its synthesized envelope/item validators.</summary>
    private sealed class ContractSchema
    {
        public required JsonSchema Root { get; init; }

        /// <summary>The schema document as a mutable tree (pointer resolution for member names).</summary>
        public required JsonNode Document { get; init; }

        /// <summary>Batch def with item validation removed; null for non-batch schemas.</summary>
        public JsonSchema? Envelope { get; private set; }

        /// <summary>The <c>$defs</c> member a batch's <c>events[]</c> items ref; null for non-batch schemas.</summary>
        public JsonSchema? Item { get; private set; }

        /// <summary>The synthesized envelope document (for member-name pointer resolution).</summary>
        public JsonNode? EnvelopeDocument { get; private set; }

        /// <summary>The synthesized item document (for member-name pointer resolution).</summary>
        public JsonNode? ItemDocument { get; private set; }

        /// <summary><c>x-max-bytes</c> transport cap, or null.</summary>
        public long? MaxBytes { get; private set; }

        public static ContractSchema Build(string key, string text, JsonSchema.Options options)
        {
            var document = JsonNode.Parse(text)!.AsObject();
            var schema = new ContractSchema
            {
                Root = JsonSchema.FromText(text, CanonicalUri(key), options),
                Document = document,
            };

            if (document.TryGetPropertyValue("x-max-bytes", out var capNode)
                && capNode is JsonValue capValue
                && capValue.TryGetValue<long>(out var cap))
            {
                schema.MaxBytes = cap;
            }

            var dialect = document.TryGetPropertyValue("$schema", out var dialectNode)
                && dialectNode is JsonValue dialectValue
                && dialectValue.TryGetValue<string>(out var d)
                ? d
                : SchemaDialect;

            // Batch detection: a $defs member whose properties.events.items.$ref
            // points at a sibling def. The loop is over the defs' own names so a
            // schema with several $defs still resolves deterministically.
            if (!document.TryGetPropertyValue("$defs", out var defsNode) || defsNode is not JsonObject defs)
            {
                return schema;
            }

            foreach (var member in defs)
            {
                if (member.Value is not JsonObject batchDef
                    || !batchDef.TryGetPropertyValue("properties", out var properties)
                    || properties is not JsonObject propertiesObject
                    || !propertiesObject.TryGetPropertyValue("events", out var eventsProperty)
                    || eventsProperty is not JsonObject eventsObject
                    || !eventsObject.TryGetPropertyValue("items", out var itemsProperty)
                    || itemsProperty is not JsonObject itemsObject
                    || !itemsObject.TryGetPropertyValue("$ref", out var reference)
                    || reference is not JsonValue referenceValue
                    || !referenceValue.TryGetValue<string>(out var referenceText)
                    || !referenceText.StartsWith("#/$defs/", StringComparison.Ordinal))
                {
                    continue;
                }

                var itemName = referenceText["#/$defs/".Length..];
                if (!defs.TryGetPropertyValue(itemName, out var itemNode) || itemNode is not JsonObject)
                {
                    continue;
                }

                // Envelope: the batch def with item validation replaced by true —
                // shape, required, minItems/maxItems and additionalProperties only.
                var envelopeDocument = (JsonObject)batchDef.DeepClone();
                ((JsonObject)envelopeDocument["properties"]!["events"]!)["items"] = true;
                envelopeDocument["$schema"] = dialect;
                var envelopeId = $"{CanonicalUri(key)}:envelope";
                envelopeDocument["$id"] = envelopeId;
                schema.EnvelopeDocument = envelopeDocument;
                schema.Envelope = JsonSchema.FromText(envelopeDocument.ToJsonString(), envelopeId, options);

                // Item: the referenced def hoisted to its own document.
                var itemDocument = (JsonObject)itemNode.DeepClone();
                itemDocument["$schema"] = dialect;
                var itemId = $"{CanonicalUri(key)}:item";
                itemDocument["$id"] = itemId;
                schema.ItemDocument = itemDocument;
                schema.Item = JsonSchema.FromText(itemDocument.ToJsonString(), itemId, options);

                break;
            }

            return schema;
        }
    }

    /// <summary>
    /// Every schema document — the on-disk roots and the synthesized
    /// envelope/item texts alike — compiles with the same lockdown: no file
    /// system or HTTP document resolution (an unresolved external
    /// <c>$ref</c> fails at load instead of touching the network), and
    /// <c>format</c> is always asserted so the result matches
    /// Ajv+ajv-formats on the corpus (AC3). Cross-file <c>$ref</c>s resolve
    /// from <paramref name="resolver"/> — the in-memory registry of the
    /// loaded contract documents only. Same values as
    /// <c>JsonSchema.Options.Default</c> except the resolution gate and the
    /// registry.
    /// </summary>
    private static JsonSchema.Options SchemaOptions(IDocumentResolver resolver) =>
        new(resolver, allowFileSystemAndHttpResolution: false, alwaysAssertFormat: true);
}
