// Proofs of apps/web/scripts/check-no-fixture-code.mjs: a production bundle passes, and every
// marker, corpus body string and missing input is reported with the file that carries it. The
// sign-in, run-views, needle-exemption and review-config cases live in sibling specs.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import {
  CLEAN_MAIN_JS,
  CORPUS_NAME,
  MARKERS,
  NON_ASCII_CORPUS_ESCAPED,
  NON_ASCII_CORPUS_NAME,
  cleanupWorkspaces,
  corpusDocument,
  escapeNonAscii,
  workspace,
} from './webFixtureLeakGate.testing.js';

afterEach(cleanupWorkspaces);

describe('no-fixture-code gate', () => {
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

  it('passes_the_product_settings_paths_and_fails_each_review_address', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    // The product routes of both tabs ARE in a production bundle (their pages are product code);
    // the marker is the `/dev/...` review address the `fileReplacements` seam adds, not the tab.
    await ws.output(
      'main-ABC123.js',
      'const routes=["orgs/:orgId/settings/secrets","orgs/:orgId/settings/policy"];',
    );
    expect(ws.run().status).toBe(0);

    for (const [marker, address] of [
      ['dev/settings-secrets', 'orgs/:orgId'],
      ['dev/settings-policy', 'orgs/:orgId'],
      ['dev/run-list', 'orgs/:orgId/projects/:projectId'],
      ['dev/run-detail', 'orgs/:orgId/projects/:projectId/runs/:runId'],
    ] as const) {
      await ws.output('main-ABC123.js', `const review=["${marker}/${address}"];`);
      const leaked = ws.run();
      expect(leaked.status).toBe(1);
      expect(leaked.stderr).toContain(marker);
    }
  });

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
    // exactly like a chunk does; both extensions are in the scanned set.
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
