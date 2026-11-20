// `variant_hash` — the one port of the server's catalog normaliser and digest, which stays the
// source of truth (src/Modules/Evidgate.Modules.Catalog/Domain/VariantNormalizer.cs,
// TextRules.cs, SharedKernel/Security/SecretShape.cs, Canonicalization/CanonicalHash.cs). The CLI
// and the Playwright reporter both import it; the reporter must not import the CLI (it loads the
// CLI lazily so a broken CLI install only degrades attachments), which is why it lives here.
// The pinned `canonicalize` package is the same library packages/cli/src/canonical.ts wraps, with
// the `sha256:<lowercase hex>` wrapper over its output. fixtures/variant-hash/vectors.json is
// the corpus this package, both consumers and the .NET variant suite assert.
//
// The normalisation rules and the digest are ported exactly. Two things cannot be, because the
// reporter is handed a JavaScript value where the server reads the author's JSON text:
//
//   * `too_large` is measured the way the server measures it — the UTF-8 byte count of
//     `JsonSerializer.Serialize(result)` under the default `JavaScriptEncoder`, not of the
//     canonical form — so `serverSerializedLength` reproduces that serialisation (escaping and
//     number notation included). The escaping rule is exact; the number notation is exact for
//     every value the reporter accepts (see `dotnetDoubleLength` in `serializedLength.ts`).
//   * A number's spelling is already lost: Playwright hands over a `number`, so `1e20` and
//     `100000000000000000000` are the same value. The server branches on the raw text
//     (`VariantNormalizer.Scalar`) — an exponent or a fraction means a `double`, plain digits mean
//     a `long` that must fit `MaxExactInteger` — while the reporter can only branch on
//     `String(value)`. Three spellings then disagree, each verified against the server:
//       - a value in [2^53, 1e21) written with an exponent or a fraction (`9.1e15`, `1e16`, `1e20`):
//         the server stores a `double` and accepts, the reporter sees plain digits past
//         `MaxExactInteger` and refuses;
//       - a value of 1e21 or more written in full digits (`1000000000000000000000`): the server's
//         `long` overflows and it refuses, the reporter sees `1e+21` and accepts;
//       - `-0.0` against `-0`: the server writes `-0` for the `double` and `0` for the `long`, the
//         reporter cannot tell them apart and writes `0`; the hash is identical, the length differs
//         by one byte.
//     The corpus records only what both sides agree on.
//
// Input is untrusted: only own enumerable properties are read (never a prototype lookup) and the
// normalised object is built with a null prototype, so a `__proto__`/`constructor` key can neither
// pollute Object.prototype nor be mistaken for an inherited member.
//
// The port is split by responsibility: this module holds the key and scalar rules behind
// `normalizeVariant`, `secretShape.ts` the secret-shape rules and `serializedLength.ts` the
// `too_large` measurement.
import { createHash } from 'node:crypto';

import canonicalize from 'canonicalize';

import { keyLooksSecret, valueLooksSecret } from './secretShape.js';
import { serverSerializedLength } from './serializedLength.js';

/** One refusal of a parameter set: where and why. The key itself is never named. */
export interface VariantProblem {
  /** JSON-pointer-like path inside the parameters (`/params/api_token`). */
  readonly path: string;
  /** A short token (`secret_key`, `secret_value`, `not_scalar`, `invalid_key`, …). */
  readonly reason: string;
}

/**
 * The normalised parameters and their content-addressed identity, or every refusal.
 * `hash` is `sha256:<lowercase hex>` over the RFC 8785 canonical form of the normalised
 * parameters; `paramsJson` is that canonical text and `params` the normalised values.
 */
export type VariantResult =
  | {
      readonly ok: true;
      readonly hash: string;
      readonly paramsJson: string;
      readonly params: Record<string, unknown>;
    }
  | { readonly ok: false; readonly problems: readonly VariantProblem[] };

/** Keys allowed per variant (VariantNormalizer.MaxKeys). */
export const MAX_VARIANT_KEYS = 32;
const MAX_KEYS = MAX_VARIANT_KEYS;

/** The largest integer a double (and so RFC 8785) represents exactly: 2^53 − 1. */
const MAX_EXACT_INTEGER = 9_007_199_254_740_991;

/** The database refuses larger parameter sets (`variant_params_check`); measured as the server does. */
const MAX_PARAMS_BYTES = 8000;

const MAX_STRING_CHARS = 256;
const MAX_ARRAY_ITEMS = 32;

/** The path prefix used in problems, matching the server's `/params` base path. */
const BASE_PATH = '/params';

/** Keys that describe a variant for humans and say nothing about what it runs: dropped before hashing. */
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

/** Keys whose string values are case-insensitive enum-like names: lower-cased and trimmed before hashing. */
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

/** VariantNormalizer.IsValidKey: `^[A-Za-z_][A-Za-z0-9_.-]{0,63}$`, culture-invariant. */
const KEY_SHAPE = /^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/;

/** TextRules.IsClean: control (Cc) and format (Cf) characters are refused, as is a lone surrogate. */
const UNCLEAN = /[\p{Cc}\p{Cf}]/u;

/** True when `text` is well-formed UTF-16 (no unpaired surrogate), as `Rune.TryGetRuneAt` requires. */
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
 * VariantNormalizer.IsValidKey().IsMatch. The trailing `$` of a JavaScript regex also matches
 * before a final line terminator, so the match must span the whole key (a key ending in
 * U+2028/U+2029 is not a control or format character and would otherwise slip through).
 */
function keyShapeOk(key: string): boolean {
  const match = KEY_SHAPE.exec(key);
  return match !== null && match[0].length === key.length;
}

/**
 * VariantNormalizer.Scalar's number branch. Canonical JSON hashes numbers as IEEE doubles, so an
 * integer a double cannot hold exactly is refused. The reporter receives a JavaScript number, not
 * its source spelling, so `String(value)` (the same shortest round-trip form `JSON.stringify`
 * emits) stands in for the raw text: a value whose shortest form has no `.`/`e`/`E` must be a safe
 * integer, and anything else must be finite.
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

/** VariantNormalizer.StringValue. */
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
  // The stored form is what must not look like a secret: an upper-cased token prefix only shows
  // after the enum-like fold, so both the text as sent and the text as stored are scanned.
  const stored = ENUM_LIKE[key.toLowerCase()] === true ? text.trim().toLowerCase() : text;
  if (valueLooksSecret(text) || valueLooksSecret(stored)) {
    problems.push({ path, reason: 'secret_value' });
    return undefined;
  }
  return stored;
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
 * Normalises one parameter set into `normalised`, collecting refusals into `problems`. Key order
 * and the first-32 rule are the server's: the 33rd key stops the walk with `too_many_keys`, which
 * is a refusal of the set, so nothing after it is examined.
 */
function collectNormalisedScalars(
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
 * Normalises variant parameters into a content-addressed identity, mirroring
 * `VariantNormalizer.Normalize` exactly: identical parameters after normalisation — key order,
 * null defaults, the case of enum-like values, non-identifying keys — map to the same variant, and
 * a parameter set that carries a secret-shaped key or value never becomes one. Parameters are flat:
 * scalars and short arrays of scalars (nested objects are refused so a secret cannot hide one level
 * down). Pure and I/O-free.
 *
 * @param params The Playwright project `metadata.evidgate.params` object, or null/undefined for none.
 */
export function normalizeVariant(params: unknown): VariantResult {
  const problems: VariantProblem[] = [];
  const normalised: Record<string, unknown> = Object.create(null) as Record<string, unknown>;

  if (params !== null && params !== undefined) {
    if (typeof params !== 'object' || Array.isArray(params)) {
      problems.push({ path: BASE_PATH, reason: 'not_object' });
      return { ok: false, problems };
    }
    collectNormalisedScalars(params, problems, normalised);
  }

  if (problems.length > 0) {
    return { ok: false, problems };
  }

  // The server measures the serialised parameters, not the canonical form, and checks the size
  // before it canonicalises; both are kept so a refusal matches.
  if (serverSerializedLength(normalised) > MAX_PARAMS_BYTES) {
    problems.push({ path: BASE_PATH, reason: 'too_large' });
    return { ok: false, problems };
  }

  const paramsJson = canonicalizeChecked(normalised);
  if (paramsJson === undefined) {
    problems.push({ path: BASE_PATH, reason: 'invalid_value' });
    return { ok: false, problems };
  }

  const hash = `sha256:${createHash('sha256').update(paramsJson, 'utf8').digest('hex')}`;
  return { ok: true, hash, paramsJson, params: normalised };
}

/**
 * RFC 8785 text of `value`, or undefined when it cannot be produced or is not self-consistent:
 * a canonical text is a fixed point of parse + canonicalize, and a runtime whose `JSON.parse`
 * disagrees would hand out a digest the server never computes. Normalised parameters are flat
 * (scalars and arrays of scalars), so no nesting cap is needed here.
 */
function canonicalizeChecked(value: unknown): string | undefined {
  const canonical = canonicalize(value);
  if (canonical === undefined) {
    return undefined;
  }
  return canonicalize(JSON.parse(canonical) as unknown) === canonical ? canonical : undefined;
}
