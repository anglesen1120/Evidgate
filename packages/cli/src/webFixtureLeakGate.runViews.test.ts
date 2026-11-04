// Proofs of apps/web/scripts/check-no-fixture-code.mjs on `--require-run-views`: the review build
// must carry the run-views tag, the flag is independent of `--allow-dev-sign-in`, and the tag stays
// a leak in the production invocation.
import { afterEach, describe, expect, it } from 'vitest';

import {
  CLEAN_MAIN_JS,
  CORPUS_NAME,
  FIXTURE_MARKERS,
  MARKERS,
  cleanupWorkspaces,
  corpusDocument,
  workspace,
} from './webFixtureLeakGate.testing.js';

afterEach(cleanupWorkspaces);

describe('no-fixture-code gate', () => {
  it('run_views_flag_requires_the_review_tag_and_reports_it', async () => {
    const flags = ['--allow-dev-sign-in', '--require-run-views'];
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('chunk-DEV123.js', 'const s="app-dev-sign-in";');

    const missing = ws.run(flags);
    expect(missing.status).toBe(1);
    expect(missing.stderr).toContain('the review output has no run views');

    await ws.output('chunk-RUN123.js', 'const o=[{id:"runs",mount:"review-run-views"}];');
    const present = ws.run(flags);
    expect(present.status).toBe(0);
    expect(present.stdout).toContain(`${FIXTURE_MARKERS.length} marker(s)`);
    expect(present.stdout).toContain('(run views present)');
  });

  it('run_views_flag_works_without_the_sign_in_flag', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('chunk-RUN123.js', 'const o=[{id:"runs",mount:"review-run-views"}];');

    expect(ws.run(['--require-run-views']).status).toBe(0);
    expect(ws.run(['--allow-dev-sign-in']).status).toBe(1);
  });

  it('run_views_flag_keeps_every_fixture_marker_forbidden', async () => {
    for (const marker of FIXTURE_MARKERS) {
      const ws = await workspace();
      await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
      await ws.output(
        'main-ABC123.js',
        `${CLEAN_MAIN_JS}const dev=${JSON.stringify(marker)};const s="app-dev-sign-in";const t="review-run-views";`,
      );

      const result = ws.run(['--allow-dev-sign-in', '--require-run-views']);

      expect(result.status, marker).toBe(1);
      expect(result.stderr).toContain(`"${marker}"`);
    }
  });

  it('the_run_views_tag_is_a_leak_without_the_flag_and_the_production_line_is_unchanged', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('main-ABC123.js', `${CLEAN_MAIN_JS}const t="review-run-views";`);

    const production = ws.run();
    expect(production.status).toBe(1);
    expect(production.stderr).toContain('"review-run-views"');

    await ws.output('main-ABC123.js', CLEAN_MAIN_JS);
    const clean = ws.run();
    expect(clean.status).toBe(0);
    expect(clean.stdout).toContain(`${MARKERS.length} marker(s)`);
    expect(clean.stdout).not.toContain('run views');
  });
});
