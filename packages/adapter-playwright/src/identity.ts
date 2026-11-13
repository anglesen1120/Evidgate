// Case/variant identity mapping. The server owns `execution_key`; the reporter resolves the case
// identity (the `evidgate.id` annotation first, else the sidecar manifest of the test file's own
// directory or an ancestor directory up to the suite's common ancestor), the variant hash (the
// server `VariantNormalizer` semantics over the project's declared params) and the target env,
// and emits them as `ext.case_id`, `ext.variant_hash` and `ext.target_env`. `execution_key` is
// derived from that triple (the server's layout, see executionKey.ts) once all three resolve, and
// stays the unresolved sentinel otherwise; the server recomputes it and refuses any other value.
import type { TestCase } from '@playwright/test/reporter';

import { brief, isCanonicalCaseId, type SidecarIndex } from './sidecar.js';
import { computeExecutionKey } from './executionKey.js';
import { normalizeVariant } from './variant.js';

/** 64 hex zeros: schema-valid, names no planned execution; marks an identity that did not resolve. */
export const PLACEHOLDER_EXECUTION_KEY = '0'.repeat(64);

/** The manifest `targetEnv` shape (contracts/manifest/manifest.v1.json `$defs.targetEnv`). */
export const TARGET_ENV_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

/** The annotation type that carries a catalog case id. */
export const EVIDGATE_ID_ANNOTATION = 'evidgate.id';

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

/** The `ext.warn` codes this adapter emits. */
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
  | 'unknown_status'
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
  /** Projects already warned about an invalid target env; created when absent. */
  targetEnvWarned?: WeakSet<object> | undefined;
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

/**
 * The identity of a test *across Playwright projects and `repeatEach` repeats*: the
 * location file plus the title path with the leading root and project segments removed.
 * Playwright's own `test.id` embeds the project id and the repeat index
 * (`[project=<id>]file+titles (repeat:N)`), so the same test running under several
 * projects (the normal case × variant design) would otherwise look like a second,
 * distinct test. Falls back to the test title for a synthetic object without a
 * `titlePath`.
 */
export function identityKey(test: TestCase): string {
  const path = typeof test.titlePath === 'function' ? test.titlePath() : undefined;
  // Playwright's titlePath is ["", projectName, file, ...describeTitles, testTitle]; the
  // root "" and the project name are dropped so one test under several projects collapses.
  const titles = Array.isArray(path) && path.length >= 2 ? path.slice(2) : [test.title];
  return `${test.location.file}\u0000${titles.join('\u0000')}`;
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
 * Resolves one test's identity: the `evidgate.id` annotation first, the sidecar second, both
 * present and different → conflict (never a silent pick). Pure except for the warning sink and
 * the optional variant memo; the stateful wrapper is {@link createIdentityResolver}.
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

  const project = test.parent?.project?.();
  const targetEnv = deps.targetEnv ?? validTargetEnv(project?.name);
  if (targetEnv !== undefined) {
    identity.target_env = targetEnv;
  } else if (deps.targetEnv === undefined && project !== undefined) {
    // Both EVIDGATE_TARGET_ENV and the project name are invalid, so no target_env is
    // emitted. Warn once per project (the project object is the memo key) and never echo
    // the rejected name.
    const warned = deps.targetEnvWarned;
    if (warned === undefined || !warned.has(project)) {
      warned?.add(project);
      deps.warn?.({
        code: 'invalid_target_env',
        message:
          'neither EVIDGATE_TARGET_ENV nor the Playwright project name is a valid target env; ' +
          'no target_env is emitted',
        path: location,
      });
    }
  }

  if (
    identity.case_id !== undefined &&
    identity.variant_hash !== undefined &&
    identity.target_env !== undefined
  ) {
    identity.execution_key = computeExecutionKey(
      identity.case_id,
      identity.variant_hash,
      identity.target_env,
    );
  }

  return identity;
}

/**
 * The stateful resolver the reporter uses: memoises each test's identity by {@link testKey} (so a
 * conflicted test is never re-mapped within the run) and reports a `duplicate_case_id` warning the
 * first time a case id is seen on a second identity (see {@link identityKey}).
 */
export function createIdentityResolver(deps: IdentityResolverDeps): IdentityResolver {
  const variantCache = deps.variantCache ?? new WeakMap<object, VariantOutcome>();
  const targetEnvWarned = deps.targetEnvWarned ?? new WeakSet<object>();
  const resolved: IdentityResolverDeps = { variantCache, targetEnvWarned };
  if (deps.sidecar !== undefined) resolved.sidecar = deps.sidecar;
  if (deps.targetEnv !== undefined) resolved.targetEnv = deps.targetEnv;
  if (deps.warn !== undefined) resolved.warn = deps.warn;

  const cache = new Map<string, Identity>();
  // The first test seen for a case id, keyed by the id. The identity key (not Playwright's
  // project-scoped `test.id`) collapses the same test across projects and `repeatEach`
  // repeats; a retry of the same test is a cache hit above and never re-warns.
  const firstUse = new Map<string, FirstUse>();

  return (test) => {
    const key = testKey(test);
    const cached = cache.get(key);
    if (cached !== undefined) return cached;

    const identity = resolveIdentity(test, resolved);
    cache.set(key, identity);

    if (identity.case_id !== undefined) {
      const location = locationOf(test);
      const file = test.location.file;
      const identityOfTest = identityKey(test);
      const first = firstUse.get(identity.case_id);
      if (first === undefined) {
        firstUse.set(identity.case_id, {
          key: identityOfTest,
          location,
          title: test.title,
          source: identity.source,
          file,
        });
      } else if (first.key !== identityOfTest) {
        // A sidecar maps one file to one case (the server's CaseDiscovery is one case per
        // file), so several tests in that file share the id by design: not a conflict.
        const sameSidecarFile =
          first.source === 'sidecar' && identity.source === 'sidecar' && first.file === file;
        if (!sameSidecarFile) {
          // Name both locations. Two tests that share one source location are told apart by
          // their (sanitised) titles so the message stays useful without echoing raw text.
          const sameLocation = first.location === location;
          const firstLabel = sameLocation
            ? `${first.location} (${brief(first.title)})`
            : first.location;
          const secondLabel = sameLocation ? `${location} (${brief(test.title)})` : location;
          resolved.warn?.({
            code: 'duplicate_case_id',
            message: `case_id ${identity.case_id} is used by two tests: ${firstLabel} and ${secondLabel}`,
            path: location,
          });
        }
      }
    }

    return identity;
  };
}

/** The first test seen for a case id, for the duplicate warning. */
interface FirstUse {
  /** {@link identityKey} of the first test: stable across projects and `repeatEach`. */
  key: string;
  /** `file:line:column` of the first test. */
  location: string;
  /** The first test's title, for the same-location message. */
  title: string;
  /** Where the first test's case id came from. */
  source: 'annotation' | 'sidecar' | undefined;
  /** The first test's file, for the sidecar one-file-one-case rule. */
  file: string;
}

interface AnnotationRead {
  id?: string | undefined;
  conflict: boolean;
}

/**
 * Reads the `evidgate.id` annotations of a test: the strict canonical case id the server
 * accepts (`isCanonicalCaseId`: the 36-character hyphenated UUID in any version and any
 * case, never the nil UUID) is lower-cased; an invalid description is reported once and
 * treated as absent; two different valid ids are a conflict.
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
    if (typeof description === 'string' && isCanonicalCaseId(description)) {
      ids.add(description.toLowerCase());
    } else {
      invalid = true;
    }
  }
  if (invalid) {
    warn?.({
      code: 'invalid_annotation',
      message: `an ${EVIDGATE_ID_ANNOTATION} annotation is not a canonical case id and was ignored`,
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
 * params object (a scalar set falls back to the project object) so 5k tests of one project
 * cost one hash; a refused set emits one `invalid_variant_params` warning naming the problem
 * paths and reasons only.
 */
function variantOutcome(test: TestCase, deps: IdentityResolverDeps): VariantOutcome {
  const project = test.parent?.project?.();
  const metadata = project?.metadata;
  const evidgate =
    typeof metadata === 'object' && metadata !== null && !Array.isArray(metadata)
      ? (metadata as Record<string, unknown>).evidgate
      : undefined;
  const declared =
    typeof evidgate === 'object' && evidgate !== null && !Array.isArray(evidgate)
      ? (evidgate as Record<string, unknown>).params
      : undefined;
  const rawParams = declared === undefined || declared === null ? EMPTY_PARAMS : declared;

  // Memoise per params object; a scalar params set has no object identity of its own, so fall
  // back to the project object — the mapping doc promises one `invalid_variant_params` warning
  // per project, not one per test.
  const cacheKey =
    typeof rawParams === 'object' && rawParams !== null
      ? rawParams
      : typeof project === 'object' && project !== null
        ? project
        : undefined;
  if (cacheKey !== undefined) {
    const cached = deps.variantCache?.get(cacheKey);
    if (cached !== undefined) return cached;
  }

  // No defensive copy: `normalizeVariant` reads only own enumerable properties (never a
  // prototype lookup) and never mutates its input, so the declared object is passed as is;
  // arrays and scalars pass through so the normaliser refuses them.
  const result = normalizeVariant(rawParams);
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
