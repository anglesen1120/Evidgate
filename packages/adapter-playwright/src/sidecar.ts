// 04.3.2-T1.1: the per-directory `evidgate.cases.yaml` sidecar loader (03.1.1-T1.3, TL T55).
// The file is untrusted input from a customer repository, so it is read as *data*: the size is
// capped before the content is read, the YAML is parsed with the core schema and refused when it
// carries an anchor, alias, tag, merge key, duplicate key or a second document, the result is
// validated against the embedded sidecar.v1 schema, and every entry is checked against the
// repository path rules so it can never name a file outside the manifest's own directory.
// Warnings are data — the identity layer (04.3.2-T1.2) turns them into `run.log` envelopes — and a
// message never echoes file content beyond the offending path or key.
import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { dirname, join, resolve, sep } from 'node:path';

import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { isAlias, isMap, isScalar, isSeq, parseDocument } from 'yaml';
import type { Node as YamlNode, ParseOptions, SchemaOptions } from 'yaml';

import { SIDECAR_SCHEMA_V1 } from './sidecarSchema.js';

/** The file name that marks a sidecar (03.1.1-T1.1). */
export const SIDECAR_FILE_NAME = 'evidgate.cases.yaml';

/** Hard cap on the sidecar text, mirroring the server reader's `MaxChars`. */
const MAX_BYTES = 1024 * 1024;
/** Nesting cap, mirroring the server reader's `MaxDepth`. */
const MAX_DEPTH = 8;
/** Node cap, mirroring the server reader's `MaxNodes`. */
const MAX_NODES = 40_000;
/** Scalar cap, mirroring the server reader's `MaxScalarChars`. */
const MAX_SCALAR_CHARS = 4096;
/** Warnings kept for one run, mirroring `CaseDiscovery.MaxProblems`. */
const MAX_WARNINGS = 2000;
/** Longest repository-relative path, mirroring `RepoPath.MaxLength`. */
const MAX_PATH_CHARS = 1024;
/** Control, format and lone-surrogate code points, refused in paths and names (`TextRules.IsClean`). */
const UNCLEAN = /[\p{Cc}\p{Cf}\p{Cs}]/u;

/**
 * Strict UTF-8: malformed bytes are refused instead of being repaired to U+FFFD (which would
 * silently map a different, replacement-character filename). A leading BOM is kept, exactly as
 * `readFileSync(path, 'utf8')` returned it before this loader owned the decode.
 */
const UTF8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

/** One problem found while reading a sidecar. `path` is the manifest, or the described file. */
export interface SidecarWarning {
  code: 'invalid_sidecar' | 'invalid_sidecar_entry' | 'sidecar_conflict';
  path: string;
  message: string;
}

/** The `location → case_id` index built from every sidecar of a run. */
export interface SidecarIndex {
  /** The case id (lower-case UUID) for an absolute test file path, undefined when unmapped or held by a conflict. */
  lookup(testFile: string): string | undefined;
  readonly warnings: readonly SidecarWarning[];
}

/** The file-system seam: `readFile` returns the sidecar bytes, or undefined when it is absent. */
export interface SidecarFs {
  readFile(path: string, maxBytes: number): Uint8Array | undefined;
}

/** One usable mapping of a sidecar: the absolute path of the described file and its id. */
interface ResolvedEntry {
  path: string;
  id: string;
}

/** One `cases[]` item, as the sidecar.v1 schema guarantees it. */
interface SidecarCase {
  file: string;
  'evidgate.id': string;
  name?: string;
}

/** A sidecar document that passed the sidecar.v1 schema. */
interface SidecarDocument {
  version: 1;
  cases: SidecarCase[];
}

/** The result of inspecting a manifest path before any content is read. */
type Inspection =
  { kind: 'absent' } | { kind: 'invalid'; reason: string } | { kind: 'file'; size: number };

type Warn = (code: SidecarWarning['code'], path: string, message: string) => void;

const defaultFs: SidecarFs = {
  readFile: (path) => readFileSync(path),
};

// Plain data only: the core schema, no merge keys, unique keys, scalar keys, and no custom tags.
// Anchors, aliases and explicit tags are refused by `inspectNode` (the server refuses them too).
const PARSE_OPTIONS: ParseOptions & SchemaOptions = {
  schema: 'core',
  merge: false,
  uniqueKeys: true,
  stringKeys: true,
  strict: true,
  customTags: [],
};

let compiled: ValidateFunction<SidecarDocument> | undefined;

/** The sidecar.v1 validator, compiled once per process. */
function schemaValidator(): ValidateFunction<SidecarDocument> {
  if (compiled === undefined) {
    const ajv = new Ajv2020({ allErrors: false, strict: false });
    ajvFormats.default(ajv);
    compiled = ajv.compile<SidecarDocument>(SIDECAR_SCHEMA_V1);
  }
  return compiled;
}

/**
 * Reads every distinct directory's `evidgate.cases.yaml` and returns the `location → case_id`
 * index. Each directory is read once; a directory without a sidecar contributes nothing and no
 * warning. A sidecar that is refused contributes no entry at all.
 */
export function loadSidecarIndex(
  testFiles: Iterable<string>,
  fs: SidecarFs = defaultFs,
): SidecarIndex {
  const warnings: SidecarWarning[] = [];
  const warn: Warn = (code, path, message) => {
    if (warnings.length < MAX_WARNINGS) {
      warnings.push({ code, path, message });
    }
  };

  const manifests = new Map<string, string>();
  for (const file of testFiles) {
    const directory = dirname(normalizePath(file));
    if (!manifests.has(directory)) {
      manifests.set(directory, join(directory, SIDECAR_FILE_NAME));
    }
  }

  const index = new Map<string, string>();
  const conflicted = new Set<string>();

  for (const manifestPath of manifests.values()) {
    const content = readSidecar(manifestPath, fs, warn);
    if (content === undefined) {
      continue;
    }

    for (const entry of parseEntries(manifestPath, content, warn)) {
      if (!existsSync(entry.path)) {
        warn(
          'invalid_sidecar_entry',
          entry.path,
          `the sidecar ${manifestPath} names a file that is not in the tree`,
        );
        continue;
      }

      const earlier = index.get(entry.path);
      if (earlier !== undefined) {
        if (earlier !== entry.id) {
          conflicted.add(entry.path);
          warn('sidecar_conflict', entry.path, 'two sidecar entries give this file different ids');
        }
        continue;
      }

      index.set(entry.path, entry.id);
    }
  }

  for (const path of conflicted) {
    index.delete(path);
  }

  return {
    lookup: (testFile) => index.get(normalizePath(testFile)),
    warnings,
  };
}

/** Inspects the manifest path and reads its text, or returns undefined with a warning. */
function readSidecar(manifestPath: string, fs: SidecarFs, warn: Warn): string | undefined {
  const inspection = inspectSidecar(manifestPath);
  if (inspection.kind === 'absent') {
    return undefined;
  }
  if (inspection.kind === 'invalid') {
    warn('invalid_sidecar', manifestPath, inspection.reason);
    return undefined;
  }
  if (inspection.size > MAX_BYTES) {
    warn('invalid_sidecar', manifestPath, 'the sidecar is larger than 1 MiB');
    return undefined;
  }

  let bytes: Uint8Array | undefined;
  try {
    bytes = fs.readFile(manifestPath, MAX_BYTES);
  } catch (error) {
    warn('invalid_sidecar', manifestPath, `the sidecar could not be read (${errorCode(error)})`);
    return undefined;
  }
  if (bytes === undefined) {
    return undefined;
  }
  if (bytes.byteLength > MAX_BYTES) {
    warn('invalid_sidecar', manifestPath, 'the sidecar is larger than 1 MiB');
    return undefined;
  }

  try {
    return UTF8.decode(bytes);
  } catch {
    // Malformed UTF-8 must never be repaired into a U+FFFD filename that maps a different file.
    warn('invalid_sidecar', manifestPath, 'the sidecar is not valid UTF-8 text');
    return undefined;
  }
}

/** lstats the manifest: absent, not a regular file (symlinks are never followed), or a file with its size. */
function inspectSidecar(manifestPath: string): Inspection {
  try {
    const stat = lstatSync(manifestPath);
    if (!stat.isFile()) {
      return { kind: 'invalid', reason: 'the sidecar is not a regular file' };
    }
    return { kind: 'file', size: stat.size };
  } catch (error) {
    const code = errorCode(error);
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      return { kind: 'absent' };
    }
    return { kind: 'invalid', reason: `the sidecar could not be inspected (${code})` };
  }
}

/** Parses and validates one sidecar; a document that is not valid contributes no entry. */
function parseEntries(manifestPath: string, content: string, warn: Warn): ResolvedEntry[] {
  const document = parseDocument(content, PARSE_OPTIONS);
  const failure = document.errors[0];
  if (failure !== undefined) {
    const position = failure.linePos?.[0];
    const where =
      position === undefined
        ? failure.code
        : `${failure.code} at line ${position.line}, column ${position.col}`;
    warn('invalid_sidecar', manifestPath, `the sidecar is not plain YAML data (${where})`);
    return [];
  }

  try {
    inspectNode(document.contents, 0, { nodes: 0 });
  } catch (error) {
    const reason = error instanceof Error ? error.message : 'unreadable';
    warn('invalid_sidecar', manifestPath, `the sidecar is not plain YAML data (${reason})`);
    return [];
  }

  let data: unknown;
  try {
    data = JSON.parse(JSON.stringify(document.toJS({ maxAliasCount: 0 })) ?? 'null');
  } catch {
    warn(
      'invalid_sidecar',
      manifestPath,
      'the sidecar is not plain YAML data (the document could not be read as data)',
    );
    return [];
  }

  const validate = schemaValidator();
  if (!validate(data)) {
    const first = validate.errors?.[0];
    const path = first?.instancePath ?? '';
    const keyword = first?.keyword ?? 'invalid';
    warn(
      'invalid_sidecar',
      manifestPath,
      `the sidecar breaks sidecar.v1 at '${path}' (${keyword})`,
    );
    return [];
  }

  return collectEntries(manifestPath, data, warn);
}

/** Turns the validated document into usable entries, warning per entry that names an unusable path. */
function collectEntries(
  manifestPath: string,
  document: SidecarDocument,
  warn: Warn,
): ResolvedEntry[] {
  const directory = dirname(manifestPath);
  const entries: ResolvedEntry[] = [];

  for (const item of document.cases) {
    if (!isRepoPath(item.file)) {
      warn(
        'invalid_sidecar_entry',
        manifestPath,
        `entry '${brief(item.file)}' is not a path inside the manifest's directory`,
      );
      continue;
    }

    const path = normalizePath(join(directory, item.file));
    if (item.name !== undefined && UNCLEAN.test(item.name)) {
      warn(
        'invalid_sidecar_entry',
        path,
        'the name of this entry has control, format or malformed Unicode characters',
      );
      continue;
    }

    entries.push({ path, id: item['evidgate.id'].toLowerCase() });
  }

  return entries;
}

/** Refuses anchors, aliases, tags, merge keys, non-scalar keys, and caps depth, nodes and scalars. */
function inspectNode(node: YamlNode | null, depth: number, state: { nodes: number }): void {
  if (node === null) {
    return;
  }
  if (depth > MAX_DEPTH || ++state.nodes > MAX_NODES) {
    throw new SidecarFormatError('the document is too deeply nested or too large');
  }
  if (isAlias(node)) {
    throw new SidecarFormatError('aliases are not allowed');
  }
  if (node.anchor !== undefined) {
    throw new SidecarFormatError('anchors are not allowed');
  }
  if (node.tag !== undefined) {
    throw new SidecarFormatError('tags are not allowed');
  }

  if (isScalar(node)) {
    if (typeof node.value === 'string' && node.value.length > MAX_SCALAR_CHARS) {
      throw new SidecarFormatError('a value is longer than 4096 characters');
    }
    return;
  }

  if (isSeq(node)) {
    for (const item of node.items) {
      inspectNode(item as YamlNode | null, depth + 1, state);
    }
    return;
  }

  if (isMap(node)) {
    for (const pair of node.items) {
      if (!isScalar(pair.key)) {
        throw new SidecarFormatError('mapping keys must be plain text');
      }
      inspectNode(pair.key, depth + 1, state);
      if (pair.key.value === '<<') {
        throw new SidecarFormatError('merge keys are not allowed');
      }
      inspectNode(pair.value as YamlNode | null, depth + 1, state);
    }
    return;
  }

  throw new SidecarFormatError('unsupported YAML construct');
}

/** The repository path rules (`RepoPath.IsValid`): relative, forward slashes, no `.`/`..` segment. */
function isRepoPath(path: string): boolean {
  if (path.length === 0 || path.length > MAX_PATH_CHARS) {
    return false;
  }
  if (path.startsWith('/') || path.endsWith('/') || path.includes('\\')) {
    return false;
  }
  if (UNCLEAN.test(path)) {
    return false;
  }
  return path
    .split('/')
    .every((segment) => segment.length > 0 && segment !== '.' && segment !== '..');
}

/** A short, single-line, control-free rendering of untrusted text for a message (`TextRules.Brief`). */
export function brief(text: string, maxChars = 120): string {
  const truncated = truncate(text, maxChars);
  let out = '';
  for (const character of truncated) {
    out += UNCLEAN.test(character) ? '?' : character;
  }
  return out.length < text.length ? `${out}…` : out;
}

/** The longest prefix that ends on a rune boundary (`TextRules.Truncate`). */
function truncate(text: string, maxChars: number): string {
  if (text.length <= maxChars) {
    return text;
  }
  let end = maxChars;
  const last = text.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) {
    end -= 1;
  }
  return text.slice(0, end);
}

/** The absolute, forward-slashed form of a path, used for both index keys and lookups. */
function normalizePath(path: string): string {
  return resolve(path).split(sep).join('/');
}

/** The error code of a node error, never its message (which can echo the path). */
function errorCode(error: unknown): string {
  const code = (error as NodeJS.ErrnoException | null)?.code;
  return typeof code === 'string' ? code : 'unreadable';
}

/** A refusal of the whole document, raised while walking the parsed YAML. */
class SidecarFormatError extends Error {}
