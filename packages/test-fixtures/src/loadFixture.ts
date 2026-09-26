// 01.6.1-T3.2 (AC6): the TypeScript half of the shared fixture corpus —
// `fixtures/<domain>/<name>/{ input.*, expected.json, meta.json }`, loaded with
// the exact shape contract of tests/Shared/FixtureLoader.cs (see
// fixtures/README.md). A directory that does not satisfy the contract is
// refused with FixtureShapeError so a malformed corpus entry can never be
// silently skipped.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { basename, join, resolve, sep } from 'node:path';

import canonicalize from 'canonicalize';

const META_FILE = 'meta.json';
const EXPECTED_FILE = 'expected.json';
const README_FILE = 'README.md';
const INPUT_PREFIX = 'input.';
const META_FIELDS = ['domain', 'kind', 'source', 'added_by'] as const;

/** The meta.json contract: exactly these four non-empty string fields. */
export interface FixtureMeta {
  readonly domain: string;
  readonly kind: string;
  readonly source: string;
  readonly addedBy: string;
}

export interface Fixture {
  readonly domain: string;
  readonly name: string;
  /** Absolute path of the fixture directory. */
  readonly dir: string;
  readonly inputPath: string;
  readonly inputText: string;
  /** Raw bytes of expected.json as text, or undefined when the file is absent. */
  readonly expectedText: string | undefined;
  /** Parsed expected.json; undefined when absent (or when the file is `null`). */
  readonly expected: unknown;
  readonly meta: FixtureMeta;
}

/**
 * A fixture directory does not satisfy the shape contract. `fixturePath` is the
 * offending directory (or the corpus root, for a missing root).
 */
export class FixtureShapeError extends Error {
  readonly fixturePath: string;

  constructor(fixturePath: string, problem: string, options?: ErrorOptions) {
    super(`${fixturePath}: ${problem}`, options);
    this.name = 'FixtureShapeError';
    this.fixturePath = fixturePath;
  }
}

const utf8 = new TextDecoder('utf-8', { fatal: true });

/** Loads one fixture directory, enforcing the whole shape contract. */
export function loadFixture(fixtureDirectory: string): Fixture {
  if (fixtureDirectory.trim() === '') {
    throw new FixtureShapeError('<empty>', 'fixture directory is empty');
  }

  const dir = resolve(fixtureDirectory);
  let entries;
  try {
    entries = readdirSync(dir);
  } catch {
    throw new FixtureShapeError(dir, 'fixture directory does not exist');
  }
  if (!statSync(dir).isDirectory()) {
    throw new FixtureShapeError(dir, 'fixture directory is not a directory');
  }

  let metaPath: string | undefined;
  let expectedPath: string | undefined;
  const inputs: string[] = [];

  for (const entry of entries) {
    const entryPath = join(dir, entry);
    if (statSync(entryPath).isDirectory()) {
      throw new FixtureShapeError(
        dir,
        `unexpected subdirectory '${entry}'; a fixture holds only input.*, meta.json, optional expected.json and optional README.md`,
      );
    }

    if (entry.startsWith(INPUT_PREFIX)) {
      inputs.push(entryPath);
      continue;
    }

    switch (entry) {
      case META_FILE:
        metaPath = entryPath;
        break;
      case EXPECTED_FILE:
        expectedPath = entryPath;
        break;
      case README_FILE:
        // Optional provenance note; the loader does not parse it.
        break;
      default:
        throw new FixtureShapeError(
          dir,
          `unexpected file '${entry}'; a fixture holds only input.*, meta.json, optional expected.json and optional README.md`,
        );
    }
  }

  if (metaPath === undefined) {
    throw new FixtureShapeError(dir, 'meta.json is missing');
  }
  if (inputs.length === 0) {
    throw new FixtureShapeError(dir, 'an input.<ext> file is missing');
  }
  if (inputs.length > 1) {
    const names = inputs
      .map((path) => basename(path))
      .sort()
      .join(', ');
    throw new FixtureShapeError(dir, `more than one input.* file: ${names}`);
  }
  const inputPath = inputs[0]!;
  const inputName = basename(inputPath);
  if (inputName === INPUT_PREFIX) {
    throw new FixtureShapeError(
      dir,
      'the input file has no extension (it must be named input.<ext>)',
    );
  }

  const meta = parseMeta(dir, metaPath);
  // The same UTF-8-BOM refusal the JSON files get applies to the input payload
  // too (AC6): readFileSync(…, 'utf8') keeps the U+FEFF while .NET's
  // File.ReadAllText would silently strip it — so the check runs on the raw
  // bytes, then the rest decodes leniently like before.
  const inputBytes = readFileSync(inputPath);
  if (hasUtf8Bom(inputBytes)) {
    throw new FixtureShapeError(dir, `'${inputName}' must be UTF-8 without BOM`);
  }
  const inputText = inputBytes.toString('utf8');

  let expectedText: string | undefined;
  let expected: unknown;
  if (expectedPath !== undefined) {
    expectedText = readUtf8Json(expectedPath, dir, EXPECTED_FILE);
    try {
      expected = JSON.parse(expectedText);
    } catch (error) {
      throw new FixtureShapeError(
        dir,
        `expected.json is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
        { cause: error },
      );
    }

    // 01.6.1 §4: expected.json, when present, IS the canonical byte sequence.
    // The TS side owns byte-exact enforcement (the .NET loader only validates
    // JSON), because `canonicalize` 5.1.0 lives here.
    const canonical = canonicalize(expected);
    if (canonical === undefined || canonical !== expectedText) {
      throw new FixtureShapeError(
        dir,
        'expected.json must be the RFC 8785 canonical byte sequence of its content (no whitespace, no trailing newline; regenerate it through the canonicalizer, never by hand)',
      );
    }
  }

  return {
    domain: meta.domain,
    name: basename(dir),
    dir,
    inputPath,
    inputText,
    expectedText,
    expected,
    meta,
  };
}

/**
 * loadFixture without throwing: null plus the refusal text when the directory is
 * not a well-formed fixture.
 */
export function tryLoadFixture(fixtureDirectory: string): {
  fixture: Fixture | null;
  problem: string | null;
} {
  try {
    return { fixture: loadFixture(fixtureDirectory), problem: null };
  } catch (error) {
    if (error instanceof FixtureShapeError) {
      return { fixture: null, problem: error.message };
    }
    throw error;
  }
}

/**
 * Loads every fixture under a corpus root. A directory is a fixture when it
 * contains `meta.json` or an `input.*` entry; anything else (rfc8785/input-style
 * byte-exact vector directories, README files) is skipped, so non-fixture
 * sibling families never break the walk while a malformed fixture — detected as
 * a candidate — always fails loudly. Returned sorted by corpus-relative path.
 */
export function loadAllFixtures(fixturesRoot: string): Fixture[] {
  if (fixturesRoot.trim() === '') {
    throw new FixtureShapeError('<empty>', 'fixtures root is empty');
  }

  const root = resolve(fixturesRoot);
  try {
    if (!statSync(root).isDirectory()) {
      throw new Error('not a directory');
    }
  } catch {
    throw new FixtureShapeError(root, 'fixtures root does not exist');
  }

  const candidates: string[] = [];
  const pending: string[] = readdirSync(root)
    .map((entry) => join(root, entry))
    .filter((path) => statSync(path).isDirectory());

  while (pending.length > 0) {
    const dir = pending.pop()!;
    const entries = readdirSync(dir);
    const isFixtureCandidate = entries.some(
      (entry) => entry === META_FILE || entry.startsWith(INPUT_PREFIX),
    );
    if (isFixtureCandidate) {
      candidates.push(dir);
      continue;
    }
    for (const entry of entries) {
      const path = join(dir, entry);
      if (statSync(path).isDirectory()) {
        pending.push(path);
      }
    }
  }

  // Ordinal (UTF-16 code-unit) comparison of the corpus-relative path, matching
  // the .NET loader's StringComparer.Ordinal: localeCompare would order
  // mixed-case corpus paths differently on the two runtimes.
  const keyed = candidates.map((dir) => {
    const relative = dir.slice(root.length).replaceAll(sep, '/');
    return { dir, key: relative.startsWith('/') ? relative.slice(1) : relative };
  });
  keyed.sort((a, b) => {
    if (a.key === b.key) {
      return 0;
    }
    return a.key < b.key ? -1 : 1;
  });
  return keyed.map((entry) => loadFixture(entry.dir));
}

function parseMeta(dir: string, metaPath: string): FixtureMeta {
  const text = readUtf8Json(metaPath, dir, META_FILE);
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (error) {
    throw new FixtureShapeError(
      dir,
      `meta.json is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
      { cause: error },
    );
  }

  // Before the shape checks, matching the .NET loader (whose Materialize walk
  // refuses a duplicate while parsing, ahead of its field checks).
  const duplicate = findDuplicateMemberName(text);
  if (duplicate !== null) {
    throw new FixtureShapeError(
      dir,
      `meta.json is not valid JSON: duplicate member name '${duplicate}'`,
    );
  }

  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new FixtureShapeError(dir, 'meta.json must be a JSON object');
  }

  const meta = value as Record<string, unknown>;
  const keys = Object.keys(meta);
  if (keys.length !== META_FIELDS.length || META_FIELDS.some((field) => !(field in meta))) {
    throw new FixtureShapeError(
      dir,
      `meta.json must have exactly the fields ${META_FIELDS.join(', ')} (no missing, no extra)`,
    );
  }

  const field = (name: (typeof META_FIELDS)[number]): string => {
    const fieldValue = meta[name];
    if (typeof fieldValue !== 'string' || fieldValue.trim() === '') {
      throw new FixtureShapeError(dir, `meta.json field '${name}' must be a non-empty string`);
    }
    return fieldValue;
  };

  return {
    domain: field('domain'),
    kind: field('kind'),
    source: field('source'),
    addedBy: field('added_by'),
  };
}

/** Reads a JSON corpus file as strict UTF-8; a BOM is an unknown shape. */
function readUtf8Json(path: string, dir: string, name: string): string {
  const bytes = readFileSync(path);
  if (hasUtf8Bom(bytes)) {
    throw new FixtureShapeError(dir, `'${name}' must be UTF-8 without BOM`);
  }
  try {
    return utf8.decode(bytes);
  } catch (error) {
    throw new FixtureShapeError(dir, `'${name}' is not valid UTF-8`, { cause: error });
  }
}

/** True when the bytes open with the UTF-8 BOM (EF BB BF). */
function hasUtf8Bom(bytes: Buffer): boolean {
  return bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
}

/**
 * The first duplicated member name in a JSON object, or null.
 *
 * A `JSON.parse` reviver cannot answer this: measured on this runtime,
 * `JSON.parse('{"a":1,"a":2}', reviver)` calls the reviver once for `a` — the
 * parser collapses a duplicate before the reviver runs (last-wins). The raw
 * text of an already-validated document is therefore scanned with a minimal
 * state machine. Each member name is decoded by `JSON.parse` on its own
 * literal, so an escaped spelling (`"dom\u0061in"`) matches its plain form, as
 * `System.Text.Json` does on the .NET side — AC6 requires both loaders to
 * refuse the same meta.json documents.
 */
function findDuplicateMemberName(text: string): string | null {
  // One frame per open container: a Set of member names for an object, null for
  // an array (array elements have no names).
  const frames: Array<Set<string> | null> = [];
  // The last significant token: '{', '[', ':', ',' or the pseudo-token 'value'.
  let previous = '';
  let index = 0;

  while (index < text.length) {
    const char = text[index];
    if (char === '{' || char === '[') {
      frames.push(char === '{' ? new Set<string>() : null);
      previous = char;
      index += 1;
    } else if (char === '}' || char === ']') {
      frames.pop();
      previous = 'value';
      index += 1;
    } else if (char === ':' || char === ',') {
      previous = char;
      index += 1;
    } else if (char === '"') {
      const start = index;
      index += 1;
      while (index < text.length && text[index] !== '"') {
        index += text[index] === '\\' ? 2 : 1;
      }
      index += 1;
      const names = frames[frames.length - 1];
      if (names !== null && names !== undefined && (previous === '{' || previous === ',')) {
        const name = JSON.parse(text.slice(start, index)) as string;
        if (names.has(name)) {
          return name;
        }
        names.add(name);
      }
      previous = 'value';
    } else if (char === ' ' || char === '\n' || char === '\r' || char === '\t') {
      index += 1;
    } else {
      previous = 'value'; // number, true, false, null
      index += 1;
    }
  }

  return null;
}
