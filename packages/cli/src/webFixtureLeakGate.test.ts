// 09.1.1-T12 (plan 09.1.1 §2 AC10 — "the production build contains no fixture code (bundle
// grep)"): the proofs of apps/web/scripts/check-no-fixture-code.mjs.
//
// The spec lives in @evidgate/cli for the same reason webLintBoundaries.test.ts does: a
// repository-level check over apps/web that needs Node runs under the Vitest `node` workspaces
// (an existing `scripts/verify.sh` stage), while `ng test` compiles only the Angular specs for
// the browser. Every case spawns the script as a child process against a synthetic build output
// and a synthetic fixture corpus, handed over through the gate's two proof-only environment
// overrides, so no case reads the real `dist/`, and nothing is written outside a temp directory.
import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';

const GATE = fileURLToPath(
  new URL('../../../apps/web/scripts/check-no-fixture-code.mjs', import.meta.url),
);

/** The dev-only marker strings, matched verbatim by the gate (see its header). */
const MARKERS = [
  'X-Evidgate-Fixture',
  'evidgate.fixture-backend',
  'fixtures/web-api',
  'provideFixtureBackend',
  'fixture_not_found',
  'dev/ui-gallery',
  'app-ui-gallery',
] as const;

/** A corpus body string long enough to count as a leak signal, and absent from production code. */
const CORPUS_NAME = 'fixture-only-project-name';

/**
 * A corpus body string with non-ASCII characters (`Dự án Đông Dương`). A bundle that escaped
 * it holds none of these characters, only their `\uXXXX` forms, so the raw needle misses it and
 * the escaped one must not.
 */
const NON_ASCII_CORPUS_NAME = 'Dự án Đông Dương';

/**
 * `NON_ASCII_CORPUS_NAME` in the escaped spelling the gate must also search: every non-ASCII code
 * unit as a lowercase `\uXXXX`. Spelled out rather than derived, so the case pins the spelling
 * itself instead of trusting a copy of the gate's own escaping helper.
 */
const NON_ASCII_CORPUS_ESCAPED = String.raw`D\u1ef1 \u00e1n \u0110\u00f4ng D\u01b0\u01a1ng`;

/**
 * Production-shaped script text that must pass: `content-type`, `application/json` and
 * `precondition_failed` are protocol vocabulary the app itself carries (see the gate's
 * `PROTOCOL_MEMBERS`), not fixture data.
 */
const CLEAN_MAIN_JS = `const client={headers:{"content-type":"application/json"},base:"/v1"};
const codes=["precondition_failed"];
`;

interface GateResult {
  readonly status: number | null;
  readonly stdout: string;
  readonly stderr: string;
}

interface WorkspaceOptions {
  readonly dist?: boolean;
  readonly corpus?: boolean;
}

interface GateWorkspace {
  readonly corpus: string;
  /** Writes `web-api/<name>/input.json`. */
  fixture(name: string, document: unknown): Promise<void>;
  /** Writes one file below the synthetic build output. */
  output(name: string, text: string): Promise<void>;
  /** Runs the gate against this workspace, with the workspace as its cwd. */
  run(): GateResult;
}

/** Runs the gate once; `env` carries the two proof-only overrides, everything else is inherited. */
function spawnGate(invocation: {
  readonly cwd: string;
  readonly dist: string;
  readonly corpus: string;
}): GateResult {
  const result = spawnSync(process.execPath, [GATE], {
    cwd: invocation.cwd,
    encoding: 'utf8',
    env: {
      ...process.env,
      EVIDGATE_WEB_DIST_DIR: invocation.dist,
      EVIDGATE_WEB_FIXTURE_DIR: invocation.corpus,
    },
  });
  return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

/** One 09.1.1-T12.1 `input.json` document, with `name` as a distinctive body string. */
function corpusDocument(name: string): unknown {
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
function escapeNonAscii(value: string): string {
  let escaped = '';
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    escaped += code > 0x7f ? `\\u${code.toString(16).padStart(4, '0')}` : value[index];
  }
  return escaped;
}

describe('no-fixture-code gate', () => {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** A synthetic build output plus corpus in one temp directory, removed after the case. */
  async function workspace(options: WorkspaceOptions = {}): Promise<GateWorkspace> {
    const root = await mkdtemp(join(tmpdir(), 'evidgate-fixture-gate-'));
    dirs.push(root);
    const dist = join(root, 'browser');
    const corpus = join(root, 'web-api');
    if (options.dist !== false) {
      await mkdir(dist, { recursive: true });
    }
    if (options.corpus !== false) {
      await mkdir(corpus, { recursive: true });
    }
    return {
      corpus,
      fixture: async (name, document) => {
        await mkdir(join(corpus, name), { recursive: true });
        await writeFile(join(corpus, name, 'input.json'), JSON.stringify(document), 'utf8');
      },
      output: async (name, text) => {
        const path = join(dist, name);
        await mkdir(dirname(path), { recursive: true });
        await writeFile(path, text, 'utf8');
      },
      run: () => spawnGate({ cwd: root, dist, corpus }),
    };
  }

  it('passes_a_clean_production_bundle', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    // The protocol slots of this document (operation id, variant, error code, headers) repeat
    // strings production code carries, so `CLEAN_MAIN_JS` may contain them and still pass.
    await ws.fixture('project-update-precondition-failed', {
      operation_id: 'updateProject',
      variant: 'precondition_failed',
      status: 412,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: {
        error: {
          code: 'precondition_failed',
          message: 'stale precondition',
          request_id: 'fixture-request',
        },
      },
    });
    await ws.output('index.html', '<app-root></app-root>');
    await ws.output('main-ABC123.js', CLEAN_MAIN_JS);
    await ws.output('styles-ABC123.css', ':root{--eg-color-surface:#ffffff}');

    const result = ws.run();

    expect(result.status).toBe(0);
    expect(result.stdout).toContain('3 file(s)');
    expect(result.stdout).toContain(`${MARKERS.length} marker(s)`);
    expect(result.stdout).toContain('fixture string(s)');
    expect(result.stderr).toBe('');
  });

  it('passes_a_permission_row_whose_resource_and_action_production_carries', async () => {
    const ws = await workspace();
    // A catalogue row as `permissions-list*/input.json` stores it: `organization` (12 characters)
    // is long enough to be a corpus needle, and the navigation registry's own literal
    // `organization:read` contains it, so only skipping `resource`/`action` lets this build pass.
    // The row's `description` is fixture data and stays a needle.
    const description = 'Read the organizations the caller is a member of.';
    await ws.fixture('permissions-list-limited', {
      operation_id: 'listPermissions',
      variant: 'limited',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: {
        items: [
          { code: 'organization:read', resource: 'organization', action: 'read', description },
        ],
      },
    });
    await ws.output('main-ABC123.js', 'const nav=[{permission:"organization:read"}];');

    expect(ws.run().status).toBe(0);

    await ws.output('main-ABC123.js', `const nav=[${JSON.stringify(description)}];`);
    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain(description);
  });

  it('still_scans_resource_and_action_outside_a_permission_row', async () => {
    const ws = await workspace();
    // `action`/`resource` are common field names; the skip applies only where `code` is exactly
    // `<resource>:<action>`. Here they are ordinary fixture data and must stay needles.
    const action = 'archive-the-staging-suite';
    await ws.fixture('audit-events', {
      operation_id: 'listAuditEvents',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: { items: [{ code: 'evt-1', resource: 'suite', action }] },
    });
    await ws.output('main-ABC123.js', `const a=${JSON.stringify(action)};`);

    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain(action);
  });

  it('skips_only_the_enumerated_words_of_the_provider_and_status_members', async () => {
    const ws = await workspace();
    // 09.2.1-T7: `provider`/`status` are no longer skipped as whole members. `github_actions`
    // (14 characters) and `pending_verification` (20) are the words the contract enumerates and
    // the step-2 page renders, so a bundle carrying them must still pass. The document carries no
    // other string of needle length, so the reported count proves the exemption did the work:
    // without the allowlist these two words are needles and the bundle holds both.
    const enumerated = { provider: 'github_actions', status: 'pending_verification' };
    await ws.fixture('ci-bindings-list', {
      operation_id: 'listCiBindings',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: {
        items: [{ id: 'cib-1', provider: enumerated.provider, status: enumerated.status }],
      },
    });
    await ws.output(
      'main-ABC123.js',
      `const words=[${JSON.stringify(enumerated.provider)},${JSON.stringify(enumerated.status)}];`,
    );

    const clean = ws.run();
    expect(clean.status).toBe(0);
    expect(clean.stdout).toContain('0 fixture string(s)');

    // Any other word under `status` is fixture data and stays a needle: the exemption is the
    // enumerated value, not the member name.
    const unexpected = 'awaiting-manual-review';
    await ws.fixture('ci-bindings-list', {
      operation_id: 'listCiBindings',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: { items: [{ id: 'cib-1', status: unexpected }] },
    });
    await ws.output('main-ABC123.js', `const s=${JSON.stringify(unexpected)};`);

    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain(unexpected);

    // The exemption is also scoped to those two member names: an enumerated word carried by any
    // other member (`name` here) is ordinary data, so it stays a needle.
    await ws.fixture('ci-bindings-list', {
      operation_id: 'listCiBindings',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: { items: [{ id: 'cib-1', name: enumerated.status }] },
    });
    await ws.output('main-ABC123.js', `const s=${JSON.stringify(enumerated.status)};`);

    const renamed = ws.run();
    expect(renamed.status).toBe(1);
    expect(renamed.stderr).toContain(enumerated.status);
  });

  for (const marker of MARKERS) {
    const slug = marker.toLowerCase().replace(/[^a-z0-9]+/g, '_');
    it(`fails_on_the_fixture_backend_marker_${slug}`, async () => {
      const ws = await workspace();
      await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
      await ws.output('main-ABC123.js', `console.log(${JSON.stringify(marker)});`);

      const result = ws.run();

      expect(result.status).toBe(1);
      expect(result.stderr).toContain(marker);
      expect(result.stderr).toContain('main-ABC123.js');
    });
  }

  it('fails_on_a_fixture_body_string', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('chunk-ABC123.js', `export const label=${JSON.stringify(CORPUS_NAME)};`);

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain(CORPUS_NAME);
    expect(result.stderr).toContain('fixtures/web-api/permissions-list/input.json');
    expect(result.stderr).toContain('chunk-ABC123.js');
  });

  it('fails_on_a_fixture_string_in_a_copied_asset', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    // `public/` is copied verbatim, so an icon or a translation catalog leaks a fixture string
    // exactly like a chunk does; both extensions joined the scanned set with 09.1.1-T12.
    await ws.output(
      'favicon.svg',
      `<svg xmlns="http://www.w3.org/2000/svg"><title>${CORPUS_NAME}</title></svg>`,
    );
    await ws.output(
      'locale/messages.xlf',
      `<trans-unit id="app.title"><source>${CORPUS_NAME}</source></trans-unit>`,
    );

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('favicon.svg');
    expect(result.stderr).toContain('messages.xlf');
    expect(result.stderr).toContain('fixtures/web-api/permissions-list/input.json');
  });

  it('fails_on_a_json_escaped_non_ascii_fixture_string', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(NON_ASCII_CORPUS_NAME));
    const escaped = escapeNonAscii(NON_ASCII_CORPUS_NAME);
    // The helper is a cross-check only: the spelled-out literal is what the bundle carries, so a
    // helper that stopped matching the gate's escaping would fail here.
    expect(escaped).toBe(NON_ASCII_CORPUS_ESCAPED);
    // The bundle carries the escapes only, so the raw needle cannot match it.
    expect(NON_ASCII_CORPUS_ESCAPED).not.toContain(NON_ASCII_CORPUS_NAME);
    await ws.output('chunk-ABC123.js', `export const label="${NON_ASCII_CORPUS_ESCAPED}";`);

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('chunk-ABC123.js');
    expect(result.stderr).toContain('input.json (JSON-escaped)');
  });

  it('fails_when_the_dist_directory_is_empty', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('production output is missing');
  });

  it('fails_when_the_dist_directory_is_missing', async () => {
    const ws = await workspace({ dist: false });
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('run `ng build` first');
  });

  it('fails_when_the_corpus_has_no_input_json', async () => {
    const ws = await workspace();
    await mkdir(join(ws.corpus, 'permissions-list'), { recursive: true });
    await writeFile(join(ws.corpus, 'permissions-list', 'meta.json'), '{}', 'utf8');
    await ws.output('index.html', '<app-root></app-root>');

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('fixture corpus is empty');
  });

  it('fails_when_the_corpus_directory_is_missing', async () => {
    const ws = await workspace({ corpus: false });
    await ws.output('index.html', '<app-root></app-root>');

    const result = ws.run();

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('fixture corpus this gate cross-checks is missing');
  });
});
