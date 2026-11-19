// Enumerates the tests of a Playwright JSON list report (`playwright test --list
// --reporter=json`) with the identity rules the adapter applies during a run: the canonical
// `evidgate.id` annotation, the per-project variant params hash, and project-relative POSIX
// paths. The CLI cannot import the adapter (the dependency arrow is one way), so the rules
// are ported here and kept in step with it.
import { realpath } from 'node:fs/promises';
import { isAbsolute, resolve } from 'node:path';

import { errnoName, readFileBounded, type BoundedFileFailure } from './boundedFile.js';
import { CliError } from './errors.js';
import { MAX_TESTS, type EnumeratedTest } from './manifestMapping.js';
import { contains } from './uploadPaths.js';

/** Hard cap on the list report, checked both before the read and on the decoded text. */
export const MAX_REPORT_BYTES = 16 * 1024 * 1024;

/** The annotation the adapter reads; its description is the canonical case id. */
const CASE_ID_ANNOTATION = 'evidgate.id';
// Mirrors the canonical case-id rule the adapter and server share: the hyphenated
// 36-character form in any case, never the nil UUID. It is duplicated because the CLI must
// not import the adapter; `sidecarRules.ts` is the other copy.
const CANONICAL_UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NIL_UUID = '00000000-0000-0000-0000-000000000000';
// Playwright's suite tree nests once per describe block; a deeper tree is a crafted report.
const MAX_SUITE_DEPTH = 32;

type HashVariant = (params: unknown) => string | undefined;

interface ProjectHashes {
  readonly byId: ReadonlyMap<string, string>;
  readonly byName: ReadonlyMap<string, string>;
}

/**
 * Enumerates the tests of one report. Suite, spec and test order is the report's own order,
 * so equal input always yields equal output. A project whose variant params have no hash and
 * a spec file that leaves the project root each refuse the whole report.
 */
export function parsePlaywrightList(
  text: string,
  hashVariant: HashVariant,
): readonly EnumeratedTest[] {
  if (Buffer.byteLength(text, 'utf8') > MAX_REPORT_BYTES) {
    throw inputTooLarge();
  }
  const root = record(parseJson(text));
  const suites = root === undefined ? undefined : array(root['suites']);
  if (root === undefined || suites === undefined) {
    throw invalid('the report has no suites list');
  }
  const projects = projectHashes(root, hashVariant);
  const tests: EnumeratedTest[] = [];
  for (const suite of suites) {
    collectSuite(suite, undefined, [], projects, tests, 1);
  }
  return tests;
}

/**
 * Reads the report file. The path is resolved and realpath'd first, so a `..` escape, an
 * absolute path outside the root and a symlink pointing outside are all refused the same
 * way; the read then refuses anything over the byte cap.
 */
export async function readPlaywrightList(path: string, root: string): Promise<string> {
  const realRoot = await realpathOrRefuse(root, 'the project root');
  const candidate = isAbsolute(path) ? resolve(path) : resolve(realRoot, path);
  const realFile = await reportFileInside(candidate, realRoot);
  const result = await readFileBounded(realFile, MAX_REPORT_BYTES);
  if (result.ok) {
    return decode(result.bytes);
  }
  throw readRefusal(result.failure);
}

/**
 * Resolves the report path and proves it sits under the root. Containment is checked on the
 * realpath, so a symlink cannot escape; when the path does not exist the lexical form is
 * checked too, so a `..` traversal is refused as a path problem rather than as unreadable.
 */
async function reportFileInside(candidate: string, root: string): Promise<string> {
  let real: string;
  try {
    real = await realpath(candidate);
  } catch (error) {
    if (!contains(root, candidate)) {
      throw outsideRoot('the list report path is outside the project root');
    }
    throw new CliError(`the list report could not be resolved (${errnoName(error)})`, {
      code: 'manifest_report_unreadable',
    });
  }
  if (!contains(root, real)) {
    throw outsideRoot('the list report path is outside the project root');
  }
  return real;
}

function collectSuite(
  raw: unknown,
  inherited: string | undefined,
  titles: readonly string[],
  projects: ProjectHashes,
  out: EnumeratedTest[],
  depth: number,
): void {
  const suite = record(raw);
  if (suite === undefined || depth > MAX_SUITE_DEPTH) {
    throw invalid('the report has a malformed suite');
  }
  const file = string(suite['file']) ?? inherited;
  const nested = array(suite['suites']) ?? [];
  const specs = array(suite['specs']) ?? [];
  if (specs.length > 0 && file === undefined) {
    throw invalid('a suite with tests names no file');
  }
  // The top-level suite's title is the file path; only describe titles belong in a title path.
  const title = string(suite['title']);
  const ownTitles = title === undefined || depth === 1 ? titles : [...titles, title];
  for (const spec of specs) {
    collectSpec(spec, file, ownTitles, projects, out);
  }
  for (const child of nested) {
    collectSuite(child, file, ownTitles, projects, out, depth + 1);
  }
}

function collectSpec(
  raw: unknown,
  suiteFile: string | undefined,
  titles: readonly string[],
  projects: ProjectHashes,
  out: EnumeratedTest[],
): void {
  const spec = record(raw);
  if (spec === undefined) {
    throw invalid('the report has a malformed spec');
  }
  const file = string(spec['file']) ?? suiteFile;
  if (file === undefined) {
    throw invalid('a spec names no file');
  }
  const specTitle = string(spec['title']) ?? '';
  const titlePath = specTitle.length === 0 ? titles : [...titles, specTitle];
  const enumeratedFile = projectRelativePath(file);
  for (const rawTest of array(spec['tests']) ?? []) {
    out.push(enumerateTest(rawTest, enumeratedFile, titlePath, projects));
    if (out.length > MAX_TESTS) {
      throw inputTooLarge();
    }
  }
}

function enumerateTest(
  raw: unknown,
  file: string,
  titlePath: readonly string[],
  projects: ProjectHashes,
): EnumeratedTest {
  const test = record(raw);
  if (test === undefined) {
    throw invalid('the report has a malformed test');
  }
  const projectId = string(test['projectId']);
  const projectName = string(test['projectName']);
  const hash =
    (projectId === undefined ? undefined : projects.byId.get(projectId)) ??
    (projectName === undefined ? undefined : projects.byName.get(projectName));
  if (hash === undefined) {
    throw variantInvalid('a test belongs to a project with no variant params hash');
  }
  const identity = readIdentity(test['annotations']);
  return {
    file,
    titlePath,
    variantHash: hash,
    ...(identity.caseId === undefined ? {} : { caseId: identity.caseId }),
    ...(identity.conflict ? { identityConflict: true } : {}),
  };
}

/**
 * The annotation rule the adapter applies: valid canonical descriptions are lowercased and
 * deduplicated, an invalid one is ignored, and two or more distinct ids mean a conflict with
 * no case id at all. A single valid id beside an ignored invalid one is not a conflict.
 */
function readIdentity(annotations: unknown): { caseId?: string; conflict: boolean } {
  const ids = new Set<string>();
  for (const raw of array(annotations) ?? []) {
    const annotation = record(raw);
    if (annotation === undefined || annotation['type'] !== CASE_ID_ANNOTATION) {
      continue;
    }
    const description = annotation['description'];
    if (typeof description === 'string' && isCanonicalCaseId(description)) {
      ids.add(description.toLowerCase());
    }
  }
  if (ids.size > 1) {
    return { conflict: true };
  }
  const [id] = ids;
  return id === undefined ? { conflict: false } : { caseId: id, conflict: false };
}

function isCanonicalCaseId(value: string): boolean {
  return CANONICAL_UUID.test(value) && value.toLowerCase() !== NIL_UUID;
}

/**
 * The report's file is relative to the Playwright root dir. Backslashes are normalised so a
 * Windows-shaped path is judged by the same rule, and any `..` that would climb above the
 * root, a drive letter or a leading slash refuses: only a path under the root is accepted.
 */
function projectRelativePath(raw: string): string {
  const file = raw.replaceAll('\\', '/');
  if (file.startsWith('/') || /^[A-Za-z]:/.test(file)) {
    throw outsideRoot('a spec file is named by an absolute path');
  }
  const segments: string[] = [];
  for (const segment of file.split('/')) {
    if (segment === '' || segment === '.') {
      continue;
    }
    if (segment === '..') {
      if (segments.length === 0) {
        throw outsideRoot('a spec file resolves outside the project root');
      }
      segments.pop();
      continue;
    }
    segments.push(segment);
  }
  if (segments.length === 0) {
    throw outsideRoot('a spec file resolves to the project root itself');
  }
  return segments.join('/');
}

function projectHashes(root: Record<string, unknown>, hashVariant: HashVariant): ProjectHashes {
  const config = record(root['config']);
  const projects = config === undefined ? undefined : array(config['projects']);
  if (projects === undefined) {
    throw invalid('the report has no config.projects list');
  }
  const byId = new Map<string, string>();
  const byName = new Map<string, string>();
  for (const raw of projects) {
    const project = record(raw);
    if (project === undefined) {
      throw invalid('the report has a malformed project');
    }
    const hash = hashVariant(variantParams(project['metadata']));
    if (hash === undefined) {
      throw variantInvalid('a project declares variant params with no valid hash');
    }
    const id = string(project['id']);
    const name = string(project['name']);
    if (id !== undefined) {
      byId.set(id, hash);
    }
    if (name !== undefined) {
      byName.set(name, hash);
    }
  }
  return { byId, byName };
}

/** `metadata.evidgate.params`, defaulting to `{}` exactly as the adapter does. */
function variantParams(metadata: unknown): unknown {
  const meta = record(metadata);
  const evidgate = meta === undefined ? undefined : record(meta['evidgate']);
  const params = evidgate === undefined ? undefined : evidgate['params'];
  return params === undefined || params === null ? {} : params;
}

async function realpathOrRefuse(path: string, subject: string): Promise<string> {
  try {
    return await realpath(path);
  } catch (error) {
    throw new CliError(`${subject} could not be resolved (${errnoName(error)})`, {
      code: 'manifest_report_unreadable',
    });
  }
}

function readRefusal(failure: BoundedFileFailure): CliError {
  switch (failure.kind) {
    case 'too_large':
      return inputTooLarge();
    case 'empty':
      return invalid('the list report is empty');
    case 'not_regular_file':
      return new CliError('the list report could not be read (not a regular file)', {
        code: 'manifest_report_unreadable',
      });
    case 'unreadable':
      return new CliError(`the list report could not be read (${failure.errno})`, {
        code: 'manifest_report_unreadable',
      });
  }
}

/** Strict UTF-8: a repaired U+FFFD could turn an unreadable report into a different one. */
function decode(bytes: Uint8Array): string {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    throw invalid('the list report is not valid UTF-8');
  }
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    throw invalid('the list report is not valid JSON');
  }
}

function inputTooLarge(): CliError {
  return new CliError(`the list report is over ${MAX_REPORT_BYTES} bytes or ${MAX_TESTS} tests`, {
    code: 'manifest_input_too_large',
  });
}

function outsideRoot(message: string): CliError {
  return new CliError(message, { code: 'manifest_path_outside_root' });
}

function variantInvalid(message: string): CliError {
  return new CliError(message, { code: 'manifest_variant_invalid' });
}

function invalid(message: string): CliError {
  return new CliError(message, { code: 'manifest_report_invalid' });
}

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function array(value: unknown): readonly unknown[] | undefined {
  return Array.isArray(value) ? value : undefined;
}

function string(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}
