// Enumerates the tests of a Playwright JSON list report (`playwright test --list
// --reporter=json`) with the identity rules the adapter applies during a run: the canonical
// `evidgate.id` annotation, the per-project variant params hash, and project-relative POSIX
// paths. The CLI cannot import the adapter (the dependency arrow is one way), so the rules
// are ported here and kept in step with it; the case-id rule lives in `caseId.ts`.
import { lstat, realpath } from 'node:fs/promises';
import { isAbsolute, resolve } from 'node:path';

import { errnoName, readFileBounded, type BoundedFileFailure } from './boundedFile.js';
import { isCanonicalCaseId } from './caseId.js';
import { CliError } from './errors.js';
import { MAX_TESTS, type EnumeratedTest } from './manifestMapping.js';
import { contains } from './uploadPaths.js';

/** Hard cap on the list report: the reader enforces it on the bytes, the parser on the text. */
export const MAX_REPORT_BYTES = 16 * 1024 * 1024;
/** Projects accepted in one report; a report beyond this is refused before any hashing. */
export const MAX_PROJECTS = 1000;

/** One enumerated test plus the project's raw variant params it was hashed from. */
export interface ListedTest extends EnumeratedTest {
  readonly variantParams: unknown;
}

/** The annotation the adapter reads; its description is the canonical case id. */
const CASE_ID_ANNOTATION = 'evidgate.id';
// A path with a C0/DEL control character cannot name a file a person wrote, and a NUL would
// truncate the path at the syscall boundary, so it is a malformed report, not an escape.
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/;
// Playwright's suite tree nests once per describe block; a deeper tree is a crafted report.
const MAX_SUITE_DEPTH = 32;

type HashVariant = (params: unknown) => string | undefined;

interface ProjectEntry {
  readonly index: number;
  readonly hash: string;
  /** The project's raw `metadata.evidgate.params`, kept so a caller can rebuild its variant. */
  readonly params: unknown;
}

interface ProjectTable {
  readonly byId: ReadonlyMap<string, ProjectEntry>;
  readonly byName: ReadonlyMap<string, ProjectEntry>;
}

/**
 * Enumerates the tests of one report. Suite, spec and test order is the report's own order,
 * so equal input always yields equal output. A report that lost tests to a collection error,
 * enumerates none, or names a project or path the rules cannot resolve is refused whole.
 */
export function parsePlaywrightList(
  text: string,
  hashVariant: HashVariant,
): readonly EnumeratedTest[] {
  return parsePlaywrightListWithParams(text, hashVariant).map((test) => ({
    file: test.file,
    titlePath: test.titlePath,
    variantHash: test.variantHash,
    ...(test.caseId === undefined ? {} : { caseId: test.caseId }),
    ...(test.identityConflict === true ? { identityConflict: true } : {}),
  }));
}

/**
 * The same enumeration, keeping each test's raw variant params: a manifest generator needs the
 * parameters themselves, not only the digest they hash to.
 */
export function parsePlaywrightListWithParams(
  text: string,
  hashVariant: HashVariant,
): readonly ListedTest[] {
  assertTextUnderCap(text);
  const root = record(parseJson(text));
  if (root === undefined) {
    throw invalid('the report is not a JSON object');
  }
  refuseCollectionErrors(optionalArray(root, 'errors', 'the report'));
  const suites = optionalArray(root, 'suites', 'the report');
  const projects = projectTable(root, hashVariant);
  const tests: ListedTest[] = [];
  for (const suite of suites) {
    collectSuite(suite, undefined, [], projects, tests, 1);
  }
  if (tests.length === 0) {
    throw invalid('the report lists no tests, so there is nothing to map');
  }
  return tests;
}

/**
 * Reads the report file. The path is resolved and realpath'd first, so a `..` escape, an
 * absolute path outside the root and a symlink pointing outside are all refused the same
 * way; the read refuses a symlink at the resolved path, a swap after it, and an over-cap size.
 */
export async function readPlaywrightList(path: string, root: string): Promise<string> {
  const realRoot = await realpathOrRefuse(root, 'the project root');
  const candidate = isAbsolute(path) ? resolve(path) : resolve(realRoot, path);
  const realFile = await reportFileInside(candidate, realRoot);
  const result = await readFileBounded(realFile, MAX_REPORT_BYTES, {
    noFollow: true,
    verify: (opened) => stillTheSameFile(realFile, realRoot, opened),
  });
  if (!result.ok) {
    throw readRefusal(result.failure);
  }
  return decode(result.bytes);
}

/**
 * Runs while the descriptor is open, so the inode cannot be recycled for a replacement. The
 * path must still name the opened file (lstat identity) and still resolve to itself inside the
 * root, which also refuses a parent directory swapped for another directory or a symlink.
 * Residual: `open` follows a symlinked parent directory, so a parent swapped for a symlink at
 * open time can make the descriptor name a file outside the root; a swap that is undone before
 * the lstat, or kept through the realpath, is refused, but one that survives the lstat and is
 * undone before the realpath is not seen. Closing that needs openat-style APIs Node lacks. The
 * report is a local input the same user already controls.
 */
async function stillTheSameFile(
  realFile: string,
  realRoot: string,
  opened: { readonly dev: number; readonly ino: number },
): Promise<boolean> {
  const named = await lstat(realFile).catch(() => undefined);
  if (named === undefined || named.dev !== opened.dev || named.ino !== opened.ino) {
    return false;
  }
  const resolved = await realpath(realFile).catch(() => undefined);
  return resolved === realFile && contains(realRoot, resolved);
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
  projects: ProjectTable,
  out: ListedTest[],
  depth: number,
): void {
  const suite = record(raw);
  if (suite === undefined || depth > MAX_SUITE_DEPTH) {
    throw invalid('the report has a malformed suite');
  }
  const file = string(suite['file']) ?? inherited;
  const nested = optionalArray(suite, 'suites', 'the suite');
  const specs = optionalArray(suite, 'specs', 'the suite');
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
  projects: ProjectTable,
  out: ListedTest[],
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
  for (const rawTest of optionalArray(spec, 'tests', 'the spec')) {
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
  projects: ProjectTable,
): ListedTest {
  const test = record(raw);
  if (test === undefined) {
    throw invalid('the report has a malformed test');
  }
  const project = testProject(test, projects);
  const identity = readIdentity(test['annotations']);
  return {
    file,
    titlePath,
    variantHash: project.hash,
    variantParams: project.params,
    ...(identity.caseId === undefined ? {} : { caseId: identity.caseId }),
    ...(identity.conflict ? { identityConflict: true } : {}),
  };
}

/**
 * Resolves the project a test ran in. An explicit `projectId` must name a declared project —
 * the name is only a fallback when the id is absent — and an id and name that disagree name
 * a report whose two fields describe different projects.
 */
function testProject(test: Record<string, unknown>, projects: ProjectTable): ProjectEntry {
  const rawId = test['projectId'];
  const rawName = test['projectName'];
  const id = rawId === undefined ? undefined : string(rawId);
  const name = rawName === undefined ? undefined : string(rawName);
  if ((rawId !== undefined && id === undefined) || (rawName !== undefined && name === undefined)) {
    throw invalid('a test names its project with a value that is not a string');
  }
  const byId = id === undefined ? undefined : projects.byId.get(id);
  if (id !== undefined && byId === undefined) {
    throw variantInvalid('a test names a project id the report does not declare');
  }
  const byName = name === undefined ? undefined : projects.byName.get(name);
  if (byName !== undefined && byId !== undefined && byName.index !== byId.index) {
    throw invalid('a test names a project id and a project name that are different projects');
  }
  const entry = byId ?? byName;
  if (entry === undefined) {
    throw variantInvalid('a test belongs to a project with no variant params hash');
  }
  return entry;
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

/**
 * The report's file is relative to the Playwright root dir. Backslashes are normalised so a
 * Windows-shaped path is judged by the same rule, and any `..` that would climb above the
 * root, a drive-qualified absolute path or a leading slash refuses: only a path under the
 * root is accepted.
 */
function projectRelativePath(raw: string): string {
  if (CONTROL_CHARACTERS.test(raw)) {
    throw invalid('a spec file path contains a control character');
  }
  if (/^[A-Za-z]:[\\/]/.test(raw)) {
    throw outsideRoot('a spec file is named by an absolute path');
  }
  const file = raw.replaceAll('\\', '/');
  if (file.startsWith('/')) {
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

/**
 * The project table: one entry per declared project, hashed from its variant params. A
 * repeated id, or a repeated name with different params, would make a test's variant depend
 * on declaration order, so both refuse.
 */
function projectTable(root: Record<string, unknown>, hashVariant: HashVariant): ProjectTable {
  const config = record(root['config']);
  if (config === undefined) {
    throw invalid('the report has no config object');
  }
  const projects = config['projects'] === undefined ? undefined : array(config['projects']);
  if (projects === undefined) {
    throw invalid('the report has no config.projects list');
  }
  if (projects.length > MAX_PROJECTS) {
    throw inputTooLarge();
  }
  const byId = new Map<string, ProjectEntry>();
  const byName = new Map<string, ProjectEntry>();
  projects.forEach((raw, index) => {
    const project = record(raw);
    if (project === undefined) {
      throw invalid('the report has a malformed project');
    }
    const params = variantParams(project['metadata']);
    const hash = hashVariant(params);
    if (hash === undefined) {
      throw variantInvalid('a project declares variant params with no valid hash');
    }
    const entry: ProjectEntry = { index, hash, params };
    const id = string(project['id']);
    const name = string(project['name']);
    if (id !== undefined) {
      if (byId.has(id)) {
        throw invalid('the report declares one project id twice');
      }
      byId.set(id, entry);
    }
    if (name !== undefined) {
      const seen = byName.get(name);
      if (seen !== undefined && seen.hash !== hash) {
        throw invalid('the report declares one project name twice with different params');
      }
      if (seen === undefined) {
        byName.set(name, entry);
      }
    }
  });
  return { byId, byName };
}

/** `metadata.evidgate.params`, defaulting to `{}` exactly as the adapter does. */
function variantParams(metadata: unknown): unknown {
  const meta = record(metadata);
  const evidgate = meta === undefined ? undefined : record(meta['evidgate']);
  const params = evidgate === undefined ? undefined : evidgate['params'];
  return params === undefined || params === null ? {} : params;
}

/**
 * A collection error means the reporter lost files, so the enumeration would silently miss
 * tests. Only the count is reported: the entries repeat reporter paths and messages.
 */
function refuseCollectionErrors(errors: readonly unknown[]): void {
  if (errors.length === 0) {
    return;
  }
  throw invalid(`the report lists ${errors.length} collection errors, so its tests are incomplete`);
}

/** An absent key is an empty list; a present value of another shape is a malformed report. */
function optionalArray(
  owner: Record<string, unknown>,
  key: string,
  subject: string,
): readonly unknown[] {
  const value = owner[key];
  if (value === undefined) {
    return [];
  }
  const items = array(value);
  if (items === undefined) {
    throw invalid(`${subject} has a ${key} value that is not a list`);
  }
  return items;
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
    case 'changed':
      return new CliError('the list report changed while it was opened', {
        code: 'manifest_report_unreadable',
      });
  }
}

/** UTF-8 takes at most three bytes per UTF-16 unit, so only a large string is re-encoded. */
function assertTextUnderCap(text: string): void {
  if (text.length * 3 > MAX_REPORT_BYTES && Buffer.byteLength(text, 'utf8') > MAX_REPORT_BYTES) {
    throw inputTooLarge();
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
  return new CliError(
    `the list report is over ${MAX_REPORT_BYTES} bytes, ${MAX_TESTS} tests or ${MAX_PROJECTS} projects`,
    { code: 'manifest_input_too_large' },
  );
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
