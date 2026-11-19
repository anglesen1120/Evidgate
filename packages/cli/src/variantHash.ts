// `variant_hash` for manifest generation: the normalisation rules and digest the reporter and the
// server apply, mirrored from `packages/adapter-playwright/src/variant.ts` because the CLI cannot
// import it (the dependency is one-way). `fixtures/variant-hash/vectors.json` pins both copies.
import { canonicalHashOfCanonical, canonicalizeJson } from './canonical.js';
import { keyLooksSecret, serverSerializedLength, valueLooksSecret } from './variantShape.js';

/** One refusal of a parameter set: where and why. The key itself is never named. */
export interface VariantProblem {
  readonly path: string;
  readonly reason: string;
}

/** Either the normalised parameters and their digest, or every refusal. */
export type VariantResult =
  | {
      readonly ok: true;
      /** `sha256:<lowercase hex>` over the RFC 8785 canonical form. */
      readonly hash: string;
      readonly paramsJson: string;
      /** The normalised parameters, the exact values the server would hash. */
      readonly params: Record<string, unknown>;
    }
  | { readonly ok: false; readonly problems: readonly VariantProblem[] };

/** Keys allowed per variant (VariantNormalizer.MaxKeys). */
const MAX_KEYS = 32;
/** The largest integer a double, and so RFC 8785, holds exactly: 2^53 − 1. */
const MAX_EXACT_INTEGER = 9_007_199_254_740_991;
/** The database refuses larger parameter sets; measured the way the server measures it. */
const MAX_PARAMS_BYTES = 8000;
const MAX_STRING_CHARS = 256;
const MAX_ARRAY_ITEMS = 32;
/** The path prefix the server reports, `/params`. */
const BASE_PATH = '/params';

/** Keys that describe a variant for humans and say nothing about what it runs. */
const NON_IDENTIFYING: Record<string, true> = {
  description: true,
  label: true,
  labels: true,
  note: true,
  notes: true,
  comment: true,
  comments: true,
  title: true,
  tags: true,
  display_name: true,
  displayname: true,
};

/** Keys whose string values are case-insensitive enum-like names. */
const ENUM_LIKE: Record<string, true> = {
  browser: true,
  engine: true,
  platform: true,
  os: true,
  mode: true,
  env: true,
  environment: true,
  channel: true,
  device: true,
  locale: true,
  viewport: true,
};

/** VariantNormalizer.IsValidKey, culture-invariant. */
const KEY_SHAPE = /^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/;
/** TextRules.IsClean: control and format characters are refused. */
const UNCLEAN = /[\p{Cc}\p{Cf}]/u;
/** The manifest `params` member's own key rule (`manifest.v1.json` `$defs/params`). */
const MANIFEST_KEY = /^[a-z][a-z0-9_]{0,63}$/;
/** The manifest `params` value rule: a string of at most this many characters. */
const MANIFEST_VALUE_MAX = 256;

/** True when `text` is well-formed UTF-16: no unpaired surrogate. */
function isWellFormed(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = text.charCodeAt(i + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      i++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}

/** TextRules.IsClean: non-null, well-formed and free of control and format characters. */
function isClean(text: string): boolean {
  return isWellFormed(text) && !UNCLEAN.test(text);
}

/**
 * VariantNormalizer.IsValidKey. A JavaScript `$` also matches before a final line terminator,
 * so the match must span the whole key.
 */
function keyShapeOk(key: string): boolean {
  const match = KEY_SHAPE.exec(key);
  return match !== null && match[0].length === key.length;
}

/**
 * VariantNormalizer.Scalar's number branch: canonical JSON hashes numbers as IEEE doubles, so an
 * integer a double cannot hold exactly is refused. The reporter receives a JavaScript number and
 * branches on its shortest round-trip spelling, the same stand-in the reporter uses.
 */
function normalizeNumber(
  value: number,
  path: string,
  problems: VariantProblem[],
): number | undefined {
  if (!Number.isFinite(value)) {
    problems.push({ path, reason: 'invalid_value' });
    return undefined;
  }
  if (!/[.eE]/.test(String(value)) && Math.abs(value) > MAX_EXACT_INTEGER) {
    problems.push({ path, reason: 'invalid_value' });
    return undefined;
  }
  return value;
}

/** VariantNormalizer.StringValue: bounded, clean, secret-free, enum-like values normalised. */
function normalizeString(
  text: string,
  key: string,
  path: string,
  problems: VariantProblem[],
): string | undefined {
  if (text.length > MAX_STRING_CHARS || !isClean(text)) {
    problems.push({ path, reason: 'invalid_value' });
    return undefined;
  }
  if (valueLooksSecret(text)) {
    problems.push({ path, reason: 'secret_value' });
    return undefined;
  }
  return ENUM_LIKE[key.toLowerCase()] === true ? text.trim().toLowerCase() : text;
}

/** VariantNormalizer.Scalar's array branch: at most 32 scalars, nulls dropped, containers refused. */
function normalizeArray(
  value: readonly unknown[],
  key: string,
  path: string,
  problems: VariantProblem[],
): unknown[] {
  if (value.length > MAX_ARRAY_ITEMS) {
    problems.push({ path, reason: 'too_many_items' });
    return [];
  }
  const array: unknown[] = [];
  let index = 0;
  for (const item of value) {
    if (item !== null && typeof item === 'object') {
      problems.push({ path: `${path}/${index}`, reason: 'not_scalar' });
    } else if (item !== null) {
      array.push(normalizeScalar(item, key, `${path}/${index}`, problems));
    }
    index++;
  }
  return array;
}

/** VariantNormalizer.Scalar: a scalar value, or undefined when it is dropped or refused. */
function normalizeScalar(
  value: unknown,
  key: string,
  path: string,
  problems: VariantProblem[],
): unknown {
  if (value === null) return undefined; // null is the default: dropped.
  switch (typeof value) {
    case 'boolean':
      return value;
    case 'number':
      return normalizeNumber(value, path, problems);
    case 'string':
      return normalizeString(value, key, path, problems);
    case 'object':
      if (Array.isArray(value)) return normalizeArray(value, key, path, problems);
      problems.push({ path, reason: 'not_scalar' });
      return undefined;
    default:
      // undefined, function, symbol, bigint: no JSON scalar the server could have seen.
      problems.push({ path, reason: 'not_scalar' });
      return undefined;
  }
}

/**
 * Normalises one parameter set into `normalised`, collecting refusals. Key order and the
 * first-32 rule are the server's: the 33rd key stops the walk, so nothing after it is examined.
 * Only own enumerable properties are read, so a `__proto__` key cannot pollute anything.
 */
function collectScalars(
  params: object,
  problems: VariantProblem[],
  normalised: Record<string, unknown>,
): void {
  let count = 0;
  for (const [name, value] of Object.entries(params)) {
    count++;
    if (count > MAX_KEYS) {
      problems.push({ path: BASE_PATH, reason: 'too_many_keys' });
      break;
    }
    // A rejected key is named by its position, never echoed: it may itself be the secret.
    const positionPath = `${BASE_PATH}/#${count}`;
    if (!keyShapeOk(name) || !isClean(name)) {
      problems.push({ path: positionPath, reason: 'invalid_key' });
      continue;
    }
    if (keyLooksSecret(name) || valueLooksSecret(name)) {
      problems.push({ path: positionPath, reason: 'secret_key' });
      continue;
    }
    const path = `${BASE_PATH}/${name}`;
    if (name.startsWith('_') || NON_IDENTIFYING[name.toLowerCase()] === true) {
      continue;
    }
    const scalar = normalizeScalar(value, name, path, problems);
    if (scalar !== undefined) {
      normalised[name] = scalar;
    }
  }
}

/**
 * Normalises the Playwright project's `metadata.evidgate.params` the way the server and the
 * reporter do: absent or null means the empty variant, and any refusal refuses the whole set.
 */
export function normalizeVariant(params: unknown): VariantResult {
  const problems: VariantProblem[] = [];
  const normalised: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
  if (params !== null && params !== undefined) {
    if (typeof params !== 'object' || Array.isArray(params)) {
      problems.push({ path: BASE_PATH, reason: 'not_object' });
      return { ok: false, problems };
    }
    collectScalars(params, problems, normalised);
  }
  if (problems.length > 0) {
    return { ok: false, problems };
  }
  if (serverSerializedLength(normalised) > MAX_PARAMS_BYTES) {
    return { ok: false, problems: [{ path: BASE_PATH, reason: 'too_large' }] };
  }
  const paramsJson = canonicalizeJson(normalised);
  return { ok: true, hash: canonicalHashOfCanonical(paramsJson), paramsJson, params: normalised };
}

/**
 * The manifest `params` object for already-normalised parameters, or undefined when the
 * manifest format cannot carry them. `manifest.v1.json` allows only lowercase keys and string
 * values, so a numeric, boolean or array value has no representation there.
 */
export function manifestParams(
  params: Readonly<Record<string, unknown>>,
): Record<string, string> | undefined {
  const entries = Object.entries(params);
  if (entries.length > MAX_KEYS) {
    return undefined;
  }
  const out: Record<string, string> = {};
  for (const [key, value] of entries) {
    if (!MANIFEST_KEY.test(key) || key.length === 0) {
      return undefined;
    }
    if (typeof value !== 'string' || value.length > MANIFEST_VALUE_MAX) {
      return undefined;
    }
    out[key] = value;
  }
  return out;
}
