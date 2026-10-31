// The Ajv-to-`details[]` mapping: the mirror of `ContractErrorMapper` on the server. One
// collector with the shared dedupe and `maxDetailErrors` cap, so a consumer holding its own
// compiled validator (the web fixture backend's OpenAPI response validator) produces
// byte-identical rows.
import type { ErrorObject, ValidateFunction } from 'ajv/dist/2020.js';

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

/** One collected `details[]` list and the ordered, de-duplicated sink feeding it. */
export interface ErrorSink {
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
export function createErrorSink(): ErrorSink {
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
export function collect(
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
