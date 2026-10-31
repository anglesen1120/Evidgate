// The OpenAPI half of the response contract: resolve an operationId to its method + path,
// and validate a response body against the already parsed `contracts/openapi.yaml`. It
// reuses the shared contract Ajv instance and error mapping, so acceptance matches the
// contract validator, and an unresolvable `$ref` becomes a row instead of a throw.
import type { Ajv2020, ValidateFunction } from 'ajv/dist/2020.js';

import { collectValidationErrors, createContractAjv } from './validate.js';
import type { ContractValidationResult } from './validate.js';

/** One operation of the document: the HTTP method and the path template (relative to `/v1`). */
export interface OpenApiResponseValidator {
  /** method + path template of an operation, or undefined. */
  operation(operationId: string): { readonly method: string; readonly path: string } | undefined;
  /**
   * Validates a response body against the JSON schema declared for
   * (operationId, status). `errors === []` means valid. Rows:
   * - `unknown_operation` — no operation in the document carries the id;
   * - `undeclared_status` — the operation declares neither the status nor a
   *   `default` response;
   * - `unexpected_body` — the declared response carries no JSON content, so the
   *   body must be absent (`undefined`/`null`);
   * - `schema_unavailable` — the schema exists but this one document cannot
   *   compile it (an unresolved external `$ref`);
   * - otherwise the mapped JSON Schema failures (`required`, `type`, …).
   */
  validateResponse(operationId: string, status: number, body: unknown): ContractValidationResult;
}

/** The HTTP methods an OpenAPI `paths` member may carry. */
const httpMethods = ['get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace'] as const;

/** `$id` of the one schema document every component schema is registered in. */
const componentsId = 'urn:evidgate:openapi:components';

/** The local `$ref` prefix addressing a member of `components.schemas`. */
const componentSchemaPrefix = '#/components/schemas/';

/** How many `$ref` hops a response object may take before it is called unresolved. */
const maxReferenceHops = 16;

/** A parsed JSON object (an OpenAPI node). */
type Json = Record<string, unknown>;

/** What the declared response allows for the body. */
type BodyContract =
  | { readonly kind: 'none' }
  | { readonly kind: 'schema'; readonly validate: ValidateFunction }
  | { readonly kind: 'unavailable' };

/** One operation of the document: route plus the responses map a body is checked against. */
interface OperationRoute {
  readonly method: string;
  readonly path: string;
  readonly responses: Json | undefined;
}

/**
 * The response validator of one parsed OpenAPI document. Compilation is lazy per
 * (operationId, status) and cached: a document is only compiled when a body is
 * actually checked against it, so an unrelated external `$ref` somewhere in the
 * document never fails the whole validator.
 */
export function createOpenApiResponseValidator(openApiDocument: unknown): OpenApiResponseValidator {
  if (!isRecord(openApiDocument)) {
    throw new TypeError('openApiDocument must be a parsed OpenAPI document object');
  }

  const document = openApiDocument;
  const operations = indexOperations(document);
  const ajv = createContractAjv();
  registerComponentSchemas(ajv, document);
  const bodies = new Map<string, BodyContract>();

  return {
    operation(operationId: string): { readonly method: string; readonly path: string } | undefined {
      const route = operations.get(operationId);
      return route === undefined ? undefined : { method: route.method, path: route.path };
    },

    validateResponse(operationId: string, status: number, body: unknown): ContractValidationResult {
      const route = operations.get(operationId);
      if (route === undefined) {
        return failure('unknown_operation');
      }

      const response = selectResponse(document, route.responses, status);
      if (response === undefined) {
        return failure('undeclared_status');
      }

      const key = `${operationId}\u0000${status}`;
      let contract = bodies.get(key);
      if (contract === undefined) {
        contract = compileBodyContract(ajv, response);
        bodies.set(key, contract);
      }

      if (contract.kind === 'none') {
        return body === undefined || body === null ? { errors: [] } : failure('unexpected_body');
      }

      if (contract.kind === 'unavailable') {
        return failure('schema_unavailable');
      }

      return { errors: collectValidationErrors(contract.validate, body) };
    },
  };
}

/** The operationId → route index of `paths` (operationId absent ⇒ not addressable). */
function indexOperations(document: Json): ReadonlyMap<string, OperationRoute> {
  const operations = new Map<string, OperationRoute>();
  const paths = document['paths'];
  if (!isRecord(paths)) {
    return operations;
  }

  for (const [path, item] of Object.entries(paths)) {
    if (!isRecord(item)) {
      continue;
    }

    for (const method of httpMethods) {
      const operation = item[method];
      if (!isRecord(operation)) {
        continue;
      }

      const operationId = operation['operationId'];
      if (typeof operationId !== 'string' || operationId.length === 0) {
        continue;
      }

      const responses = operation['responses'];
      operations.set(operationId, {
        method: method.toUpperCase(),
        path,
        responses: isRecord(responses) ? responses : undefined,
      });
    }
  }

  return operations;
}

/**
 * Registers every `components.schemas` member in one `$defs` document, with the
 * component references inside them re-addressed to that document. Nothing is
 * compiled here: Ajv resolves `$ref` only when a validator that reaches it is
 * compiled.
 */
function registerComponentSchemas(ajv: Ajv2020, document: Json): void {
  const components = document['components'];
  const schemas = isRecord(components) ? components['schemas'] : undefined;
  if (!isRecord(schemas)) {
    return;
  }

  const definitions: Json = {};
  for (const [name, schema] of Object.entries(schemas)) {
    definitions[name] = rewriteComponentRefs(schema);
  }

  ajv.addSchema({ $id: componentsId, $defs: definitions });
}

/**
 * Deep-copies one schema node, rewriting every local component reference from
 * `#/components/schemas/<name>` to `urn:evidgate:openapi:components#/$defs/<name>`
 * (any pointer tail after the name is kept, so a sub-schema reference stays one).
 */
function rewriteComponentRefs(node: unknown): unknown {
  if (Array.isArray(node)) {
    return (node as unknown[]).map((entry) => rewriteComponentRefs(entry));
  }

  if (!isRecord(node)) {
    return node;
  }

  const rewritten: Json = {};
  for (const [key, value] of Object.entries(node)) {
    if (key === '$ref' && typeof value === 'string' && value.startsWith(componentSchemaPrefix)) {
      rewritten[key] = `${componentsId}#/$defs/${value.slice(componentSchemaPrefix.length)}`;
    } else {
      rewritten[key] = rewriteComponentRefs(value);
    }
  }

  return rewritten;
}

/**
 * The response object for `status`: the exact status key, then `default`. A
 * response that is a local `$ref` is followed; an unresolvable one counts as
 * not declared (a broken document cannot be checked against).
 */
function selectResponse(
  document: Json,
  responses: Json | undefined,
  status: number,
): Json | undefined {
  if (responses === undefined) {
    return undefined;
  }

  const selected = responses[String(status)] ?? responses['default'];
  return followLocalRefs(document, selected);
}

/** Follows a chain of local `$ref` objects to the first concrete node. */
function followLocalRefs(document: Json, value: unknown): Json | undefined {
  let current = value;

  for (let hops = 0; hops <= maxReferenceHops; hops++) {
    if (!isRecord(current)) {
      return undefined;
    }

    const reference = current['$ref'];
    if (reference === undefined) {
      return current;
    }

    current = typeof reference === 'string' ? resolvePointer(document, reference) : undefined;
  }

  return undefined;
}

/** Resolves one `#/json/pointer` reference inside the parsed document. */
function resolvePointer(document: Json, reference: string): unknown {
  if (!reference.startsWith('#/')) {
    return undefined;
  }

  let current: unknown = document;
  for (const raw of reference.slice(2).split('/')) {
    if (!isRecord(current)) {
      return undefined;
    }

    const segment = raw.replace(/~1/g, '/').replace(/~0/g, '~');
    current = current[segment];
  }

  return current;
}

/**
 * The body verdict of one declared response: no JSON content, a compiled schema,
 * or `unavailable` when the schema cannot be compiled from this document.
 */
function compileBodyContract(ajv: Ajv2020, response: Json): BodyContract {
  const schema = jsonBodySchema(response);
  if (schema === undefined) {
    return { kind: 'none' };
  }

  try {
    // Both OpenAPI and JSON Schema 2020-12 allow a boolean schema. An object one
    // is compiled as-is, with only its component references re-addressed: the
    // document needs no `$id` of its own because every reference it makes is
    // absolute, so structurally equal responses share one Ajv validator.
    const validate =
      typeof schema === 'boolean'
        ? ajv.compile(schema)
        : ajv.compile(rewriteComponentRefs(schema) as Json);
    return { kind: 'schema', validate };
  } catch {
    return { kind: 'unavailable' };
  }
}

/** The JSON schema of the response's JSON content, or undefined when it carries none. */
function jsonBodySchema(response: Json): unknown {
  const content = response['content'];
  if (!isRecord(content)) {
    return undefined;
  }

  const mediaTypes = Object.keys(content);
  const mediaType =
    mediaTypes.find((candidate) => candidate.toLowerCase() === 'application/json') ??
    mediaTypes.find((candidate) => isJsonMediaType(candidate));
  if (mediaType === undefined) {
    return undefined;
  }

  const entry = content[mediaType];
  return isRecord(entry) ? entry['schema'] : undefined;
}

/** A media type ending in `/json` or carrying `+json` is a JSON body. */
function isJsonMediaType(mediaType: string): boolean {
  const lower = mediaType.toLowerCase();
  return lower.endsWith('/json') || lower.includes('+json');
}

/** A result carrying one document-level row (the 00 §9.2 `path` of the document itself). */
function failure(reason: string): ContractValidationResult {
  return { errors: [{ index: null, path: '', reason }] };
}

/** Narrowing guard: a parsed JSON object, never an array or null. */
function isRecord(value: unknown): value is Json {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
