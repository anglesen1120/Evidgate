// 01.6.1-T3.2 (AC6): golden-file helpers. A fixture's expected.json is the
// RFC 8785 canonical byte sequence of its content (fixtures/README.md), so
// equality is asserted on canonical bytes — a golden that was hand-prettified
// fails here, and a value whose canonical form differs fails here. `canonicalize`
// 5.1.0 is the pinned JCS implementation (DECISIONS §4.2), the same dependency
// @evidgate/cli hashes with.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import canonicalize from 'canonicalize';

/** A golden file is unreadable, not JSON, or not in canonical form. */
export class GoldenFileError extends Error {
  readonly goldenPath: string;

  constructor(goldenPath: string, problem: string, options?: ErrorOptions) {
    super(`${goldenPath}: ${problem}`, options);
    this.name = 'GoldenFileError';
    this.goldenPath = goldenPath;
  }
}

/**
 * The RFC 8785 canonical text of a parsed JSON value.
 *
 * @throws {TypeError} when the value is not valid JSON data (undefined,
 * functions, symbols, `NaN`, `±Infinity`, lone surrogates).
 */
export function canonicalJson(value: unknown): string {
  const canonical = canonicalize(value);
  if (canonical === undefined) {
    throw new TypeError('value cannot be canonicalized: it is not valid JSON data');
  }
  return canonical;
}

/**
 * Reads a golden file and proves its bytes are already the canonical form of
 * its content (same byte-exact convention as fixtures/rfc8785/output: no
 * insignificant whitespace, no trailing newline). Returns the canonical text.
 *
 * @throws {GoldenFileError} when the file is missing, not JSON, or not
 * canonical — a pretty-printed or hand-edited golden is a rejection, not a
 * reformat.
 */
export function assertCanonicalGolden(goldenPath: string): string {
  const absolute = resolve(goldenPath);
  let text: string;
  try {
    text = readFileSync(absolute, 'utf8');
  } catch (error) {
    throw new GoldenFileError(
      absolute,
      `golden file does not exist or is not readable: ${error instanceof Error ? error.message : String(error)}`,
      { cause: error },
    );
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new GoldenFileError(
      absolute,
      `golden file is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
      { cause: error },
    );
  }

  const canonical = canonicalJson(parsed);
  if (canonical !== text) {
    throw new GoldenFileError(
      absolute,
      'golden file is not the RFC 8785 canonical byte sequence of its content; regenerate it through canonicalize(), never by hand-formatting',
    );
  }
  return canonical;
}

/** Parses a golden file after {@link assertCanonicalGolden} passes. */
export function readGolden(goldenPath: string): unknown {
  return JSON.parse(assertCanonicalGolden(goldenPath));
}

/**
 * Asserts a produced value's canonical form equals the golden file's canonical
 * bytes. The comparison is on canonical text, so member order in `actual` is
 * irrelevant but every byte of the stored golden is pinned.
 *
 * @throws {GoldenFileError} listing the expected and actual canonical text on
 * mismatch (the texts are single-line JCS output).
 */
export function assertGoldenEqual(actual: unknown, goldenPath: string): void {
  const expected = assertCanonicalGolden(goldenPath);
  const actualCanonical = canonicalJson(actual);
  if (actualCanonical !== expected) {
    throw new GoldenFileError(
      resolve(goldenPath),
      `value differs from the golden file\nexpected: ${expected}\nactual:   ${actualCanonical}`,
    );
  }
}

/** `00 §7` digest over the file's raw bytes: `sha256:<lowercase hex>`. */
export function sha256OfFile(path: string): string {
  return `sha256:${createHash('sha256')
    .update(readFileSync(resolve(path)))
    .digest('hex')}`;
}
