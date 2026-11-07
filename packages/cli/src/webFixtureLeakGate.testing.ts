// Shared fixtures for the proofs of apps/web/scripts/check-no-fixture-code.mjs. The spec lives in
// @evidgate/cli for the same reason webLintBoundaries.test.ts does: a repository-level check over
// apps/web that needs Node runs under the Vitest `node` workspaces (an existing `scripts/verify.sh`
// stage), while `ng test` compiles only the Angular specs for the browser. Every case spawns the
// script as a child process against a synthetic build output, fixture corpus and contract, handed
// over through the gate's three proof-only environment overrides, so no case reads the real
// `dist/`, only the repository-contract pin reads `contracts/openapi.yaml`, and nothing is
// written outside a temp directory.
import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const GATE = fileURLToPath(
  new URL('../../../apps/web/scripts/check-no-fixture-code.mjs', import.meta.url),
);

/**
 * The dev-only marker strings, matched verbatim by the gate (see its header). The split mirrors
 * the gate's `FIXTURE_MARKERS` / `DEV_SIGN_IN_MARKERS`: the whole list is forbidden in the
 * production invocation, and the sign-in half is what `--allow-dev-sign-in` exempts.
 */
export const FIXTURE_MARKERS = [
  'X-Evidgate-Fixture',
  'evidgate.fixture-backend',
  'fixtures/web-api',
  'provideFixtureBackend',
  'fixture_not_found',
  'dev/ui-gallery',
  'app-ui-gallery',
  // The phase-later review addresses of the same `fileReplacements` seam.
  'dev/settings-secrets',
  'dev/settings-policy',
  'dev/run-list',
  'dev/run-detail',
  'dev/suite-composition',
] as const;

/** The six Development-only sign-in markers, exempt under `--allow-dev-sign-in` only. */
export const DEV_SIGN_IN_MARKERS = [
  'app-dev-sign-in',
  '/v1/dev/session',
  'Dev sign-in',
  'Đăng nhập phát triển',
  // The same sign-in through the generated SDK: the operation name and its bare `url`.
  'createDevSession',
  '/dev/session',
] as const;

/** Every marker in the gate's order: fixture first, sign-in last. */
export const MARKERS = [...FIXTURE_MARKERS, ...DEV_SIGN_IN_MARKERS] as const;

/**
 * The contract every workspace starts with. The gate parses the whole repository contract
 * (13.8k lines, over 100 ms per spawn) for property names only the error-envelope exemption
 * uses, so a case that does not name its own contract gets this one instead of the real file.
 */
const DEFAULT_CONTRACT = `components:
  schemas:
    ProofOnly:
      properties:
        contract_property_probe: { type: string }
`;

/** A corpus body string long enough to count as a leak signal, and absent from production code. */
export const CORPUS_NAME = 'fixture-only-project-name';

/**
 * A corpus body string with non-ASCII characters (`Dự án Đông Dương`). A bundle that escaped
 * it holds none of these characters, only their `\uXXXX` forms, so the raw needle misses it and
 * the escaped one must not.
 */
export const NON_ASCII_CORPUS_NAME = 'Dự án Đông Dương';

/**
 * `NON_ASCII_CORPUS_NAME` in the escaped spelling the gate must also search: every non-ASCII code
 * unit as a lowercase `\uXXXX`. Spelled out rather than derived, so the case pins the spelling
 * itself instead of trusting a copy of the gate's own escaping helper.
 */
export const NON_ASCII_CORPUS_ESCAPED = String.raw`D\u1ef1 \u00e1n \u0110\u00f4ng D\u01b0\u01a1ng`;

/**
 * Production-shaped script text that must pass: `content-type`, `application/json` and
 * `precondition_failed` are protocol vocabulary the app itself carries (see the gate's
 * `PROTOCOL_MEMBERS`), not fixture data.
 */
export const CLEAN_MAIN_JS = `const client={headers:{"content-type":"application/json"},base:"/v1"};
const codes=["precondition_failed"];
`;

export interface GateResult {
  readonly status: number | null;
  readonly stdout: string;
  readonly stderr: string;
}

export interface WorkspaceOptions {
  readonly dist?: boolean;
  readonly corpus?: boolean;
}

export interface GateWorkspace {
  readonly corpus: string;
  /** Writes `web-api/<name>/input.json`. */
  fixture(name: string, document: unknown): Promise<void>;
  /** Writes one file below the synthetic build output. */
  output(name: string, text: string): Promise<void>;
  /**
   * Replaces the workspace's default synthetic contract: `text` is written to the workspace,
   * `null` names a path that does not exist.
   */
  contract(text: string | null): Promise<void>;
  /**
   * Runs against the repository's own `contracts/openapi.yaml`, not the synthetic default. A
   * later `contract()` call replaces the default again, so the last of the two calls wins.
   */
  useRepositoryContract(): void;
  /** Runs the gate against this workspace, with the workspace as its cwd. */
  run(args?: readonly string[]): GateResult;
}

/** Runs the gate once; `env` carries the three proof-only overrides, everything else is inherited. */
function spawnGate(invocation: {
  readonly cwd: string;
  readonly dist: string;
  readonly corpus: string;
  readonly contract?: string | undefined;
  readonly args?: readonly string[] | undefined;
}): GateResult {
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    EVIDGATE_WEB_DIST_DIR: invocation.dist,
    EVIDGATE_WEB_FIXTURE_DIR: invocation.corpus,
  };
  if (invocation.contract === undefined) {
    // The repository-contract case reads the file the gate resolves, never a caller's override.
    delete env.EVIDGATE_WEB_CONTRACT_FILE;
  } else {
    env.EVIDGATE_WEB_CONTRACT_FILE = invocation.contract;
  }
  const result = spawnSync(process.execPath, [GATE, ...(invocation.args ?? [])], {
    cwd: invocation.cwd,
    encoding: 'utf8',
    env,
  });
  return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

/** One corpus `input.json` document, with `name` as a distinctive body string. */
export function corpusDocument(name: string): unknown {
  return {
    operation_id: 'listPermissions',
    variant: 'ok',
    status: 200,
    headers: { 'content-type': 'application/json' },
    latency_ms: 0,
    body: { items: [{ id: 'perm-0192f0d1-7c4e-7a1f-9b5d-2c6d3e4f5a60', name }] },
  };
}

/**
 * `value` with every non-ASCII UTF-16 code unit written as a lowercase `\uXXXX` escape — the form
 * the gate also searches (and the form a minifier that escapes non-ASCII text emits). Written
 * over code units, not code points, so a surrogate pair becomes its two escapes exactly as
 * `JSON.stringify` plus such an escape pass produces.
 */
export function escapeNonAscii(value: string): string {
  let escaped = '';
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    escaped += code > 0x7f ? `\\u${code.toString(16).padStart(4, '0')}` : value[index];
  }
  return escaped;
}

/** The temp roots the sibling specs created; `cleanupWorkspaces` empties it after each case. */
const directories: string[] = [];

/** A synthetic build output plus corpus in one temp directory, removed by `cleanupWorkspaces`. */
export async function workspace(options: WorkspaceOptions = {}): Promise<GateWorkspace> {
  const root = await mkdtemp(join(tmpdir(), 'evidgate-fixture-gate-'));
  directories.push(root);
  const dist = join(root, 'browser');
  const corpus = join(root, 'web-api');
  if (options.dist !== false) {
    await mkdir(dist, { recursive: true });
  }
  if (options.corpus !== false) {
    await mkdir(corpus, { recursive: true });
  }
  const contractFile = join(root, 'openapi.yaml');
  await writeFile(contractFile, DEFAULT_CONTRACT, 'utf8');
  let repositoryContract = false;
  return {
    corpus,
    contract: async (text) => {
      repositoryContract = false;
      if (text === null) {
        await rm(contractFile, { force: true });
      } else {
        await writeFile(contractFile, text, 'utf8');
      }
    },
    useRepositoryContract: () => {
      repositoryContract = true;
    },
    fixture: async (name, document) => {
      await mkdir(join(corpus, name), { recursive: true });
      await writeFile(join(corpus, name, 'input.json'), JSON.stringify(document), 'utf8');
    },
    output: async (name, text) => {
      const path = join(dist, name);
      await mkdir(dirname(path), { recursive: true });
      await writeFile(path, text, 'utf8');
    },
    run: (args) =>
      spawnGate({
        cwd: root,
        dist,
        corpus,
        contract: repositoryContract ? undefined : contractFile,
        args,
      }),
  };
}

/** Removes every workspace the specs created; call from the file's `afterEach`. */
export async function cleanupWorkspaces(): Promise<void> {
  await Promise.all(directories.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
}
