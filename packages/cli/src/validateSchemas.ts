// Loading and compiling the contract schema corpus: one Ajv instance, every document
// registered under its canonical URI before anything compiles, so a cross-file `$ref`
// resolves from that in-memory registry only. The registry is the loaded set, never the
// file system.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, basename } from 'node:path';

import type { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';

import { createContractAjv } from './validateAjv.js';
import {
  bodyTooLargeReason,
  collect,
  createErrorSink,
  invalidJsonReason,
  type ContractValidationResult,
} from './validateErrors.js';

const eventsMember = 'events';

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

/** The `$schema` a compiled wrapper document declares; the 2020-12 dialect by default. */
function resolveDialect(document: Record<string, unknown>): string {
  return typeof document['$schema'] === 'string'
    ? document['$schema']
    : 'https://json-schema.org/draft/2020-12/schema';
}

/**
 * Compiles the batch pair a definition carries, or `undefined` when the definition is not a
 * batch: its `properties.events.items.$ref` must point at a sibling `$defs` member that is
 * an object. Envelope and item are compiled in that order, as on the server.
 */
function compileBatchValidators(
  ajv: Ajv2020,
  key: string,
  def: unknown,
  defs: Record<string, unknown>,
  dialect: string,
): { readonly envelope: ValidateFunction; readonly item: ValidateFunction } | undefined {
  if (typeof def !== 'object' || def === null) {
    return undefined;
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
    return undefined;
  }

  const itemName = reference.slice('#/$defs/'.length);
  const itemDef = defs[itemName];
  if (typeof itemDef !== 'object' || itemDef === null) {
    return undefined;
  }

  // Envelope: the batch def with item validation replaced by true — shape,
  // required, minItems/maxItems and additionalProperties only.
  const envelopeDocument = JSON.parse(JSON.stringify(def)) as Record<string, unknown>;
  const envelopeEvents = envelopeDocument['properties'] as Record<string, unknown>;
  const envelopeItems = envelopeEvents[eventsMember] as Record<string, unknown>;
  envelopeItems['items'] = true;
  envelopeDocument['$schema'] = dialect;
  envelopeDocument['$id'] = `urn:evidgate:contract:${key}:envelope`;
  const envelope = ajv.compile(envelopeDocument);

  // Item: the referenced def hoisted to its own document.
  const itemDocument = JSON.parse(JSON.stringify(itemDef)) as Record<string, unknown>;
  itemDocument['$schema'] = dialect;
  itemDocument['$id'] = `urn:evidgate:contract:${key}:item`;
  return { envelope, item: ajv.compile(itemDocument) };
}

/**
 * One registry entry: the document's own validator, its `x-max-bytes` cap, and — for a
 * batch schema — the envelope and item pair built from its first batch definition.
 */
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

  const dialect = resolveDialect(document);

  // Batch detection — identical to ContractSchemaSet.Build: a $defs member whose
  // properties.events.items.$ref points at a sibling def.
  const defs = document['$defs'];
  if (defs === undefined || typeof defs !== 'object' || defs === null) {
    return schema;
  }

  for (const def of Object.values(defs as Record<string, unknown>)) {
    const batch = compileBatchValidators(ajv, key, def, defs as Record<string, unknown>, dialect);
    if (batch === undefined) {
      continue;
    }
    schema.envelope = batch.envelope;
    schema.item = batch.item;
    break;
  }

  return schema;
}
