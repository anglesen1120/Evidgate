// 04.3.2-T1.2/T1.4: case/variant identity mapping. The server owns `execution_key`
// (00 §7); the reporter resolves the case identity (the `evidgate.id` annotation first,
// else the per-directory sidecar manifest), the variant hash (the server
// `VariantNormalizer` semantics over the project's declared params) and the target env,
// and emits them as `ext.case_id`, `ext.variant_hash` and `ext.target_env`.
// `execution_key` stays the schema-valid placeholder behind the injectable resolver seam
// (coordinator decision 4): the reporter never hashes it.
import type { TestCase } from '@playwright/test/reporter';

import type { SidecarIndex } from './sidecar.js';
import { normalizeVariant } from './variant.js';

/** 64 hex zeros — a schema-valid stand-in until the server derives the real key. */
export const PLACEHOLDER_EXECUTION_KEY = '0'.repeat(64);

/** The manifest `targetEnv` shape (contracts/manifest/manifest.v1.json `$defs.targetEnv`). */
export const TARGET_ENV_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

/** The annotation type that carries a catalog case id (00 §7, 03.1.1). */
export const EVIDGATE_ID_ANNOTATION = 'evidgate.id';

/** Any-version UUID; the resolved id is lower-cased before use. */
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The shared empty parameter set: every project without declared params hashes once. */
const EMPTY_PARAMS: Record<string, never> = {};

export interface Identity {
  execution_key: string;
  /** The resolved catalog case id, absent when unmapped or conflicted. */
  case_id?: string;
  /** `sha256:<64 hex>` over the canonical normalised params (server semantics). */
  variant_hash?: string;
  /** The target env: `EVIDGATE_TARGET_ENV`, else the Playwright project name. */
  target_env?: string;
  /** Set (never `false`) when annotation and sidecar disagree; no `case_id` is emitted. */
  identity_conflict?: true;
  /** Where the case id came from; absent when unmapped or conflicted. */
  source?: 'annotation' | 'sidecar';
}

/**
 * Maps a test case to its execution identity. The default is the built-in resolver
 * ({@link createIdentityResolver}); a caller may inject its own via the reporter's
 * `identity` option, which then wins.
 */
export type IdentityResolver = (test: TestCase) => Identity;

/** The `ext.warn` codes this adapter emits (04.3.2 §5 "Warning channel"). */
export type WarningCode =
  | 'duplicate_case_id'
  | 'identity_conflict'
  | 'invalid_annotation'
  | 'invalid_variant_params'
  | 'invalid_target_env'
  | 'invalid_sidecar'
  | 'invalid_sidecar_entry'
  | 'sidecar_conflict'
  | 'unmapped'
  | 'warnings_truncated';

/** The payload carried under `ext.warn` on a warning `run.log` envelope. */
export interface WarningPayload {
  code: WarningCode;
  message: string;
  /** `file:line:column` of the test the warning is about, when there is one. */
  path?: string;
  /** A count for the summary warnings (`unmapped`, `warnings_truncated`). */
  count?: number;
}

/** The memoised variant outcome for one params object (a project's declared params). */
export interface VariantOutcome {
  hash?: string;
}

export interface IdentityResolverDeps {
  /** Sidecar index built once at `onBegin`; absent means no sidecar mapping. */
  sidecar?: SidecarIndex | undefined;
  /** `EVIDGATE_TARGET_ENV`, validated in the ctor; undefined falls back to the project name. */
  targetEnv?: string | undefined;
  /** Warning sink; called at most once per distinct warning. */
  warn?: ((warning: WarningPayload) => void) | undefined;
  /** Per-project variant memo; created when absent. */
  variantCache?: WeakMap<object, VariantOutcome> | undefined;
}

/**
 * The identity key of a test: its Playwright `id` (stable across retries and shards),
 * falling back to `location:title` for a synthetic object without one.
 */
export function testKey(test: TestCase): string {
  const id = test.id;
  if (typeof id === 'string' && id.length > 0) return id;
  return `${locationOf(test)}:${test.title}`;
}

/** `file:line:column` of a test, the location form the warning messages name. */
export function locationOf(test: TestCase): string {
  const { file, line, column } = test.location;
  return `${file}:${line}:${column}`;
}

/** A valid manifest `targetEnv`, or undefined when the value does not match the shape. */
export function validTargetEnv(value: unknown): string | undefined {
  return typeof value === 'string' && TARGET_ENV_PATTERN.test(value) ? value : undefined;
}

/**
 * Resolves one test's identity (04.3.2 §5): the `evidgate.id` annotation first, the
 * sidecar second, both present and different → conflict (never a silent pick). Pure
 * except for the warning sink and the optional variant memo; the stateful wrapper is
 * {@link createIdentityResolver}.
 */
export function resolveIdentity(test: TestCase, deps: IdentityResolverDeps): Identity {
  const location = locationOf(test);
  const annotation = readAnnotation(test, deps.warn, location);
  const sidecarId = deps.sidecar?.lookup(test.location.file);

  let caseId: string | undefined;
  let source: 'annotation' | 'sidecar' | undefined;
  let conflict = annotation.conflict;

  if (!conflict) {
    if (annotation.id !== undefined && sidecarId !== undefined) {
      if (annotation.id === sidecarId) {
        caseId = annotation.id;
        source = 'annotation';
      } else {
        conflict = true;
      }
    } else if (annotation.id !== undefined) {
      caseId = annotation.id;
      source = 'annotation';
    } else if (sidecarId !== undefined) {
      caseId = sidecarId;
      source = 'sidecar';
    }
  }

  if (conflict) {
    const reason = annotation.conflict
      ? 'two different evidgate.id annotations'
      : 'annotation and sidecar disagree';
    deps.warn?.({
      code: 'identity_conflict',
      message: `identity conflict for ${location}: ${reason}; no case_id is emitted`,
      path: location,
    });
  }

  const identity: Identity = { execution_key: PLACEHOLDER_EXECUTION_KEY };
  if (caseId !== undefined && source !== undefined) {
    identity.case_id = caseId;
    identity.source = source;
  }
  if (conflict) identity.identity_conflict = true;

  const variant = variantOutcome(test, deps);
  if (variant.hash !== undefined) identity.variant_hash = variant.hash;

  const targetEnv = deps.targetEnv ?? validTargetEnv(test.parent?.project?.()?.name);
  if (targetEnv !== undefined) identity.target_env = targetEnv;

  return identity;
}

/**
 * The stateful resolver the reporter uses: memoises each test's identity by
 * {@link testKey} (so a conflicted test is never re-mapped within the run, §6) and
 * reports a `duplicate_case_id` warning the first time a case id is seen on a second
 * location.
 */
export function createIdentityResolver(deps: IdentityResolverDeps): IdentityResolver {
  const variantCache = deps.variantCache ?? new WeakMap<object, VariantOutcome>();
  const resolved: IdentityResolverDeps = { variantCache };
  if (deps.sidecar !== undefined) resolved.sidecar = deps.sidecar;
  if (deps.targetEnv !== undefined) resolved.targetEnv = deps.targetEnv;
  if (deps.warn !== undefined) resolved.warn = deps.warn;

  const cache = new Map<string, Identity>();
  const firstLocation = new Map<string, string>();

  return (test) => {
    const key = testKey(test);
    const cached = cache.get(key);
    if (cached !== undefined) return cached;

    const identity = resolveIdentity(test, resolved);
    cache.set(key, identity);

    if (identity.case_id !== undefined) {
      const location = locationOf(test);
      const first = firstLocation.get(identity.case_id);
      if (first === undefined) {
        firstLocation.set(identity.case_id, location);
      } else if (first !== location) {
        resolved.warn?.({
          code: 'duplicate_case_id',
          message: `case_id ${identity.case_id} is used by two tests: ${first} and ${location}`,
          path: location,
        });
      }
    }

    return identity;
  };
}

interface AnnotationRead {
  id?: string | undefined;
  conflict: boolean;
}

/**
 * Reads the `evidgate.id` annotations of a test: a valid UUID (any version) is
 * lower-cased; an invalid description is reported once and treated as absent; two
 * different valid ids are a conflict.
 */
function readAnnotation(
  test: TestCase,
  warn: ((warning: WarningPayload) => void) | undefined,
  location: string,
): AnnotationRead {
  const annotations = test.annotations ?? [];
  const ids = new Set<string>();
  let invalid = false;
  for (const annotation of annotations) {
    if (annotation?.type !== EVIDGATE_ID_ANNOTATION) continue;
    const description = annotation.description;
    if (typeof description === 'string' && UUID_PATTERN.test(description)) {
      ids.add(description.toLowerCase());
    } else {
      invalid = true;
    }
  }
  if (invalid) {
    warn?.({
      code: 'invalid_annotation',
      message: `an ${EVIDGATE_ID_ANNOTATION} annotation is not a UUID and was ignored`,
      path: location,
    });
  }
  if (ids.size === 0) return { conflict: false };
  if (ids.size > 1) return { conflict: true };
  return { id: [...ids][0], conflict: false };
}

/**
 * The variant hash for a test's project: `normalizeVariant` over the project's declared
 * params (`metadata.evidgate.params`, own properties only; absent → `{}`). Memoised per
 * params object so 5k tests of one project cost one hash; a refused set emits one
 * `invalid_variant_params` warning naming the problem paths and reasons only.
 */
function variantOutcome(test: TestCase, deps: IdentityResolverDeps): VariantOutcome {
  const metadata = test.parent?.project?.()?.metadata;
  const evidgate =
    typeof metadata === 'object' && metadata !== null && !Array.isArray(metadata)
      ? (metadata as Record<string, unknown>).evidgate
      : undefined;
  const declared =
    typeof evidgate === 'object' && evidgate !== null && !Array.isArray(evidgate)
      ? (evidgate as Record<string, unknown>).params
      : undefined;
  const rawParams = declared === undefined || declared === null ? EMPTY_PARAMS : declared;

  const cacheKey = typeof rawParams === 'object' && rawParams !== null ? rawParams : undefined;
  if (cacheKey !== undefined) {
    const cached = deps.variantCache?.get(cacheKey);
    if (cached !== undefined) return cached;
  }

  // A defensive own-property copy for a plain object; arrays and scalars pass through so
  // the normaliser refuses them.
  const params =
    typeof rawParams === 'object' && rawParams !== null && !Array.isArray(rawParams)
      ? Object.fromEntries(Object.entries(rawParams))
      : rawParams;

  const result = normalizeVariant(params);
  const outcome: VariantOutcome = result.ok ? { hash: result.hash } : {};
  if (!result.ok) {
    // Paths and reasons only — a refused value may itself be the secret, never echoed.
    const problems = result.problems
      .map((problem) => `${problem.path}: ${problem.reason}`)
      .join(', ');
    deps.warn?.({
      code: 'invalid_variant_params',
      message: `variant params refused: ${problems}`,
      path: locationOf(test),
    });
  }
  if (cacheKey !== undefined) deps.variantCache?.set(cacheKey, outcome);
  return outcome;
}
