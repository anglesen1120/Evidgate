// The CLI half of the dual-runtime validators: Ajv 8 against the 2020-12 meta-schema with
// `format` asserted, matching Corvus.Json.Validator's `alwaysAssertFormat` on the server,
// and mapping engine output to the same `details[]` rows the server emits so both suites
// agree on the parity corpus. The registry is the loaded set only, never the file system.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, basename } from 'node:path';

import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ErrorObject, ValidateFunction } from 'ajv/dist/2020.js';
// ajv-formats ships CJS: the plugin is module.exports (typed `default`). Under
// NodeNext ESM import, the binding is the module namespace whose `default`
// member is the plugin — the call is therefore `ajvFormats.default(ajv)` (the
// shape both Node's interop and TS's CJS modelling agree on).
import ajvFormats from 'ajv-formats';

/**
 * The one Ajv configuration every Evidgate schema check shares: the
 * 2020-12 meta-schema with `ajv-formats` installed (format assertions on, like
 * the server's `alwaysAssertFormat`) and `strict: false`, so OpenAPI 3.1
 * documents carrying `x-*` keywords and JSON-Schema-2020-12-only constructs
 * compile. `loadContractSchemas` and the OpenAPI response validator
 * (`createOpenApiResponseValidator`) both build their engine here — never a
 * second configuration.
 */
export function createContractAjv(): Ajv2020 {
  const ajv = new Ajv2020({ allErrors: true, strict: false });
  ajvFormats.default(ajv);
  return ajv;
}

const eventsMember = 'events';

/** Reason token for a body over the schema's `x-max-bytes` cap (00 §9.3). */
export const bodyTooLargeReason = 'body_too_large';

/** Reason token for a body that is not JSON at all. */
export const invalidJsonReason = 'invalid_json';

/** Reason for the terminal marker row appended when the detail cap fires. */
export const truncatedReason = 'truncated';

/**
 * Hard cap on collected `details[]` rows: at most this many mapped errors are
 * kept; a further distinct row replaces nothing — the cap emits one terminal
 * `{index: null, path: '', reason: 'truncated'}` marker as the last element.
 * The server applies the identical rule (fixtures/contracts/README.md).
 */
export const maxDetailErrors = 100;

/**
 * Composite keywords whose self-row carries the failure in both engines —
 * `anyOf`, `oneOf`, `not`, `contains` are kept verbatim (their inner
 * diagnostics are dropped below, matching the server's Corvus output which
 * never surfaces them).
 */

/**
 * Composite keywords that never surface as a leaf `details[]` row: Ajv emits an
 * `if` row for a failed `then` (and wrapper rows for `allOf`), while Corvus
 * wraps leaf failures in the composite locations — and folds a `propertyNames`
 * failure into the inner keyword's row. Both runtimes drop them so the
 * surviving (index, path, reason) sets are identical.
 */
const wrapperKeywords: Record<string, true> = {
  allOf: true,
  if: true,
  then: true,
  else: true,
  dependentSchemas: true,
  propertyNames: true,
  properties: true,
  items: true,
  additionalItems: true,
};

/**
 * Composite subtree segments that disqualify a row: Ajv reports diagnostics
 * from inside a satisfied `not` (the negation passes, the row is noise), from
 * inside the `if` keyword (the `then`/`else` children carry the failure), and
 * inside `anyOf`/`oneOf`/`contains` branches (the self-row reports instead).
 * Corvus never emits any of these rows.
 */
const maskedSegments: Record<string, true> = {
  if: true,
  not: true,
  anyOf: true,
  oneOf: true,
  contains: true,
};

/** One `details[]` row of the 00 §9.2 error envelope. */
export interface ContractValidationError {
  /** 0-based batch item index; null for document-level errors. */
  readonly index: number | null;
  /** Absolute JSON pointer to the failing member; `""` names the document itself. */
  readonly path: string;
  /** The failed constraint as a lower_snake token (e.g. `required`, `format`, `max_items`). */
  readonly reason: string;
}

/** The outcome of validating one document: empty `errors` means accepted. */
export interface ContractValidationResult {
  readonly errors: readonly ContractValidationError[];
}

/** The compiled validators of one contract schema file. */
interface ContractSchema {
  /** The whole schema document (non-batch fallback). */
  readonly root: ValidateFunction;
  /** The schema document itself (JSON), kept for nothing else but `x-max-bytes`. */
  readonly document: Record<string, unknown>;
  /** Batch def with item validation removed; undefined for non-batch schemas. */
  readonly envelope?: ValidateFunction;
  /** The `$defs` member a batch's `events[]` items $ref; undefined for non-batch schemas. */
  readonly item?: ValidateFunction;
  /** `x-max-bytes` transport cap, or undefined. */
  readonly maxBytes?: number;
}

/**
 * Every compiled contract schema, keyed by file name without `.json`
 * (`event-envelope.v1`, `capability.v1`). Mirrors
 * `Evidgate.BuildingBlocks.Contracts.Validation.ContractSchemaSet`.
 */
export class ContractSchemaSet {
  private readonly schemas: ReadonlyMap<string, ContractSchema>;

  public constructor(schemas: ReadonlyMap<string, ContractSchema>) {
    this.schemas = schemas;
  }

  public get keys(): readonly string[] {
    return [...this.schemas.keys()];
  }

  public has(schemaKey: string): boolean {
    return this.schemas.has(schemaKey);
  }

  /** The schema's `x-max-bytes` cap, or undefined. */
  public maxBytes(schemaKey: string): number | undefined {
    return this.get(schemaKey).maxBytes;
  }

  /**
   * Validates a raw JSON body (text or UTF-8 bytes): the `x-max-bytes` cap, then
   * JSON well-formedness (`invalid_json`), then the schema — a byte-capped or
   * unparseable body never reaches the engine. Identical order to the server's
   * `ContractSchemaSet.Validate`.
   */
  public validate(schemaKey: string, body: string | Uint8Array): ContractValidationResult {
    const schema = this.get(schemaKey);
    const byteLength = typeof body === 'string' ? Buffer.byteLength(body, 'utf8') : body.byteLength;
    if (schema.maxBytes !== undefined && byteLength > schema.maxBytes) {
      return {
        errors: [{ index: null, path: '', reason: bodyTooLargeReason }],
      };
    }

    let document: unknown;
    try {
      document = JSON.parse(typeof body === 'string' ? body : new TextDecoder().decode(body));
    } catch {
      return { errors: [{ index: null, path: '', reason: invalidJsonReason }] };
    }

    return this.validateDocument(schema, document);
  }

  /**
   * Validates an already-parsed document (the filter path: the server binds the
   * body as `JsonElement` and validates before DTO materialization).
   */
  public validateParsed(schemaKey: string, document: unknown): ContractValidationResult {
    return this.validateDocument(this.get(schemaKey), document);
  }

  private get(schemaKey: string): ContractSchema {
    const schema = this.schemas.get(schemaKey);
    if (schema === undefined) {
      throw new Error(`unknown contract schema '${schemaKey}' (loaded: ${this.keys.join(', ')})`);
    }
    return schema;
  }

  /**
   * Batch dispatch — identical to the .NET implementation: an object carrying an
   * `events` member on a batch-capable schema validates as envelope + items
   * (envelope errors suppress item diagnostics entirely); anything else validates
   * as a single item, or against the whole schema when there is no batch/item
   * pair (capability document).
   */
  private validateDocument(schema: ContractSchema, document: unknown): ContractValidationResult {
    const sink = createErrorSink();
    const add = sink.add;

    if (
      schema.envelope !== undefined &&
      schema.item !== undefined &&
      typeof document === 'object' &&
      document !== null &&
      !Array.isArray(document) &&
      eventsMember in document
    ) {
      collect(schema.envelope, document, null, '', add);
      const events = (document as Record<string, unknown>)[eventsMember];
      if (sink.errors.length === 0 && Array.isArray(events)) {
        events.forEach((item, index) => {
          collect(schema.item!, item, index, `/events/${index}`, add);
        });
      }
      return { errors: sink.errors };
    }

    collect(schema.item ?? schema.root, document, null, '', add);
    return { errors: sink.errors };
  }
}

/** One collected `details[]` list and the ordered, de-duplicated sink feeding it. */
interface ErrorSink {
  readonly errors: ContractValidationError[];
  readonly add: (error: ContractValidationError) => boolean;
}

/**
 * Ordered row collection + O(1) membership for the mapped triples: dedupe on
 * index|path|reason and hard-cap at `maxDetailErrors` — a further distinct row
 * appends the terminal 'truncated' marker and stops the collection (the
 * identical rule the server applies). Shared by `validateDocument` and the
 * exported `collectValidationErrors` so a consumer holding its own compiled
 * validator (the OpenAPI response validator) produces byte-identical rows.
 */
function createErrorSink(): ErrorSink {
  const errors: ContractValidationError[] = [];
  const seen = new Set<string>();
  let truncated = false;
  const add = (error: ContractValidationError): boolean => {
    if (truncated) {
      return false;
    }

    const key = `${error.index ?? ''}|${error.path}|${error.reason}`;
    if (seen.has(key)) {
      return true;
    }

    seen.add(key);
    if (errors.length >= maxDetailErrors) {
      const marker = { index: null, path: '', reason: truncatedReason };
      errors.push(marker);
      seen.add(`|${marker.path}|${marker.reason}`);
      truncated = true;
      return false;
    }

    errors.push(error);
    return true;
  };

  return { errors, add };
}

/**
 * Ajv-side mapping of one error to the shared row shape — the mirror of
 * `ContractErrorMapper` in `Evidgate.BuildingBlocks.Contracts.Validation`.
 * `prefix` re-anchors item-relative pointers under `/events/<i>` for the
 * absolute 00 §9.2 path.
 */
function collect(
  validate: ValidateFunction,
  instance: unknown,
  index: number | null,
  prefix: string,
  add: (error: ContractValidationError) => boolean,
): void {
  if (validate(instance)) {
    return;
  }

  let mapped = 0;
  for (const error of validate.errors ?? []) {
    const row = mapError(error, index, prefix);
    if (row !== null) {
      mapped++;
      if (!add(row)) {
        // Detail cap fired — the collector emitted its terminal 'truncated'
        // marker; stop spending on mapping.
        return;
      }
    }
  }

  if (mapped === 0) {
    // A document the engine calls invalid can never surface as valid: even a
    // row that maps to nothing leaves a generic self-leaf trace.
    add({ index, path: prefix, reason: 'invalid' });
  }
}

function mapError(
  error: ErrorObject,
  index: number | null,
  prefix: string,
): ContractValidationError | null {
  const keyword = error.keyword;

  // Ajv emits the 'if' row itself ("must match 'then' schema") for a failed
  // conditional and wrapper rows for allOf/dependentSchemas/items/properties/
  // propertyNames — dropped like the server's wrapper rows (the server folds
  // propertyNames into the inner keyword's merged row, which Ajv already
  // reports at #/propertyNames/<kw>).
  if (keyword in wrapperKeywords) {
    return null;
  }

  // Diagnostics of a satisfied composite subtree are never evidence — the
  // reporting keyword's self row carries the failure instead. schemaPath leads
  // with '#', so the first real segment is index 1.
  const segments = error.schemaPath.split('/');
  for (const segment of segments.slice(1, -1)) {
    if (segment in maskedSegments) {
      return null;
    }
  }

  let instancePath = error.instancePath;
  let member: string | null = null;
  const params = error.params as Record<string, unknown> | undefined;
  if (keyword === 'required' && typeof params?.['missingProperty'] === 'string') {
    member = params['missingProperty'];
  } else if (
    keyword === 'additionalProperties' &&
    typeof params?.['additionalProperty'] === 'string'
  ) {
    member = params['additionalProperty'];
  } else if (keyword === 'uniqueItems' && /\/\d+$/.test(instancePath)) {
    // Some engine versions locate uniqueItems at the duplicate element; the
    // shared convention points at the array itself.
    instancePath = instancePath.slice(0, instancePath.lastIndexOf('/'));
  }

  // Member names join the path with JSON pointer escapes (`~` → `~0`, `/` → `~1`);
  // the reason is the keyword in lower_snake — the same token the server emits.
  const path =
    prefix +
    instancePath +
    (member === null ? '' : `/${member.replace(/~/g, '~0').replace(/\//g, '~1')}`);
  return {
    index,
    path,
    reason: keyword.replace(/([a-z0-9])([A-Z])/g, '$1_$2').toLowerCase(),
  };
}

/**
 * The shared Ajv-to-row mapping over one already-parsed document: exactly the
 * collector `ContractSchemaSet.validateDocument` runs (same dedupe, same
 * `maxDetailErrors` cap and 'truncated' marker), exported for consumers that
 * hold their own compiled validator — the web fixture backend's OpenAPI
 * response validator (`openapiResponse.ts`) is one. `prefix` re-anchors
 * item-relative pointers under `/events/<i>`, as in the batch path.
 */
export function collectValidationErrors(
  validate: ValidateFunction,
  instance: unknown,
  index: number | null = null,
  prefix = '',
): readonly ContractValidationError[] {
  const sink = createErrorSink();
  collect(validate, instance, index, prefix, sink.add);
  return sink.errors;
}

/**
 * The canonical URI a schema is addressed by — in a sibling document's `$ref`
 * and in Ajv's registry alike: `urn:evidgate:contract:<key>`. Mirrors
 * `ContractSchemaSet.CanonicalUri` on the server.
 */
export function canonicalUri(schemaKey: string): string {
  return `urn:evidgate:contract:${schemaKey}`;
}

/** `contracts/` sub-directories holding JSON Schema documents, each searched recursively. */
const schemaRoots = ['schemas', 'adapter', 'manifest', 'catalog'] as const;

/** One loaded schema file: its path (for the duplicate-key message) and raw text. */
export interface SchemaDocument {
  readonly file: string;
  readonly text: string;
}

/**
 * Every `*.json` document under the schema roots, keyed by file name without
 * `.json` (the key every runtime, and the `fixtures/contracts/vectors/<key>/`
 * corpus directory, addresses a schema by). A duplicate key is a contract bug,
 * not a last-one-wins.
 */
function collectSchemaDocuments(contractsDirectory: string): Map<string, SchemaDocument> {
  const documents = new Map<string, SchemaDocument>();
  for (const root of schemaRoots) {
    const directory = join(contractsDirectory, root);
    if (!existsSync(directory)) {
      continue;
    }

    for (const file of listJsonFiles(directory)) {
      const key = basename(file, '.json');
      const existing = documents.get(key);
      if (existing !== undefined) {
        throw new Error(
          `duplicate contract schema key '${key}': '${existing.file}' and '${file}' both load under it`,
        );
      }

      documents.set(key, { file, text: readFileSync(file, 'utf8') });
    }
  }

  if (documents.size === 0) {
    throw new Error(`no contract schema (*.json) found under '${contractsDirectory}'`);
  }

  return documents;
}

/** The `*.json` files of a directory tree, depth-first, ordinally sorted at every level. */
function listJsonFiles(directory: string): string[] {
  const entries = readdirSync(directory, { withFileTypes: true }).sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0,
  );

  const files: string[] = [];
  for (const entry of entries) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...listJsonFiles(path));
    } else if (entry.name.endsWith('.json')) {
      files.push(path);
    }
  }

  return files;
}

/**
 * Loads every `*.json` schema under `<contractsDirectory>/schemas`,
 * `<contractsDirectory>/adapter` (recursively, so `adapter/operations/*.v1.json`
 * is included) and `<contractsDirectory>/manifest`, compiling them through one
 * Ajv 2020-12 instance with `ajv-formats` (format assertions on, like the
 * server's `alwaysAssertFormat`). Keyed by file name without extension.
 *
 * Every document is registered in the same Ajv instance under its canonical URI
 * before anything is compiled, so a cross-file `$ref` (for example
 * `execute.v1.json` → `urn:evidgate:contract:event-envelope.v1#/$defs/event`)
 * resolves from that in-memory registry only — nothing is fetched from the file
 * system or the network.
 */
export function loadContractSchemas(contractsDirectory: string): ContractSchemaSet {
  return compileSchemaDocuments(collectSchemaDocuments(contractsDirectory));
}

/** One contract schema registry from an already-collected document set. */
export function compileSchemaDocuments(documents: Map<string, SchemaDocument>): ContractSchemaSet {
  const ajv = createContractAjv();

  // The canonical URI is the address a sibling file may $ref and the key Ajv
  // resolves it under; the file's own `$id` is its file name, which nothing
  // references. One addressed copy per key is registered AND compiled, so a
  // document can never be validated against a different text than the one a
  // cross-file reference sees.
  const addressed = new Map<string, Record<string, unknown>>();
  for (const [key, document] of documents) {
    const copy = {
      ...(JSON.parse(document.text) as Record<string, unknown>),
      $id: canonicalUri(key),
    };
    ajv.addSchema(copy);
    addressed.set(key, copy);
  }

  const schemas = new Map<string, ContractSchema>();
  for (const [key, document] of addressed) {
    schemas.set(key, buildSchema(ajv, key, document));
  }

  return new ContractSchemaSet(schemas);
}

function buildSchema(ajv: Ajv2020, key: string, document: Record<string, unknown>): ContractSchema {
  const root = ajv.getSchema(canonicalUri(key));
  if (root === undefined) {
    throw new Error(`contract schema '${key}' was not registered under '${canonicalUri(key)}'`);
  }

  const schema: {
    root: ValidateFunction;
    document: Record<string, unknown>;
    envelope?: ValidateFunction;
    item?: ValidateFunction;
    maxBytes?: number;
  } = {
    root,
    document,
  };

  const maxBytes = document['x-max-bytes'];
  if (typeof maxBytes === 'number') {
    schema.maxBytes = maxBytes;
  }

  const dialect =
    typeof document['$schema'] === 'string'
      ? document['$schema']
      : 'https://json-schema.org/draft/2020-12/schema';

  // Batch detection — identical to ContractSchemaSet.Build: a $defs member whose
  // properties.events.items.$ref points at a sibling def.
  const defs = document['$defs'];
  if (defs === undefined || typeof defs !== 'object' || defs === null) {
    return schema;
  }

  for (const def of Object.values(defs as Record<string, unknown>)) {
    if (typeof def !== 'object' || def === null) {
      continue;
    }

    const events = (def as Record<string, unknown>)['properties'];
    const itemsRef =
      typeof events === 'object' && events !== null
        ? (
            (events as Record<string, unknown>)[eventsMember] as Record<string, unknown> | undefined
          )?.['items']
        : undefined;
    const reference =
      typeof itemsRef === 'object' && itemsRef !== null
        ? (itemsRef as Record<string, unknown>)['$ref']
        : undefined;
    if (typeof reference !== 'string' || !reference.startsWith('#/$defs/')) {
      continue;
    }

    const itemName = reference.slice('#/$defs/'.length);
    const itemDef = (defs as Record<string, unknown>)[itemName];
    if (typeof itemDef !== 'object' || itemDef === null) {
      continue;
    }

    // Envelope: the batch def with item validation replaced by true — shape,
    // required, minItems/maxItems and additionalProperties only.
    const envelopeDocument = JSON.parse(JSON.stringify(def)) as Record<string, unknown>;
    const envelopeEvents = envelopeDocument['properties'] as Record<string, unknown>;
    const envelopeItems = envelopeEvents[eventsMember] as Record<string, unknown>;
    envelopeItems['items'] = true;
    envelopeDocument['$schema'] = dialect;
    envelopeDocument['$id'] = `urn:evidgate:contract:${key}:envelope`;
    schema.envelope = ajv.compile(envelopeDocument);

    // Item: the referenced def hoisted to its own document.
    const itemDocument = JSON.parse(JSON.stringify(itemDef)) as Record<string, unknown>;
    itemDocument['$schema'] = dialect;
    itemDocument['$id'] = `urn:evidgate:contract:${key}:item`;
    schema.item = ajv.compile(itemDocument);

    break;
  }

  return schema;
}
