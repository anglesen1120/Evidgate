// Proofs of apps/web/scripts/check-no-fixture-code.mjs on the Development-only sign-in: the default
// invocation forbids it, `--allow-dev-sign-in` permits exactly the six sign-in needles, and the
// review build must still carry a mounted form even though the generated SDK always has the route.
import { afterEach, describe, expect, it } from 'vitest';

import {
  CLEAN_MAIN_JS,
  CORPUS_NAME,
  FIXTURE_MARKERS,
  cleanupWorkspaces,
  corpusDocument,
  workspace,
} from './webFixtureLeakGate.testing.js';

afterEach(cleanupWorkspaces);

describe('no-fixture-code gate', () => {
  it('rejects_development_sign_in_in_a_lazy_production_chunk', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('main-ABC123.js', CLEAN_MAIN_JS);
    expect(ws.run().status).toBe(0);
    await ws.output(
      'chunk-DEV123.js',
      'const selector="app-dev-sign-in";const endpoint="/v1/dev/session";',
    );
    const result = ws.run();
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('app-dev-sign-in');
    expect(result.stderr).toContain('/v1/dev/session');

    // The generated SDK spells the same route `url: '/dev/session'` under its operation name, so a
    // chunk (or its source map) that kept the operation instead of the app's service is caught too.
    await ws.output('chunk-SDK456.js', 'const op="createDevSession";const url="/dev/session";');
    const sdkLeak = ws.run();
    expect(sdkLeak.status).toBe(1);
    expect(sdkLeak.stderr).toContain('chunk-SDK456.js: "createDevSession"');
    expect(sdkLeak.stderr).toContain('chunk-SDK456.js: "/dev/session"');
  });

  it('default_mode_fails_the_sign_in_the_review_flag_permits', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('chunk-DEV123.js', 'const selector="app-dev-sign-in";');

    const production = ws.run();
    expect(production.status).toBe(1);
    expect(production.stderr).toContain('"app-dev-sign-in"');

    const review = ws.run(['--allow-dev-sign-in']);
    expect(review.status).toBe(0);
    expect(review.stderr).toBe('');
  });

  it('review_mode_keeps_every_fixture_marker_forbidden', async () => {
    // The exemption is the six sign-in needles only: a leaked fixture backend or a leaked review
    // route is still a failure in the review build, which is the whole reason the flag exists
    // rather than a blanket skip.
    for (const marker of FIXTURE_MARKERS) {
      const ws = await workspace();
      await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
      await ws.output(
        'main-ABC123.js',
        `${CLEAN_MAIN_JS}const dev=${JSON.stringify(marker)};const s="app-dev-sign-in";`,
      );

      const result = ws.run(['--allow-dev-sign-in']);

      expect(result.status, marker).toBe(1);
      expect(result.stderr).toContain(`"${marker}"`);
    }
  });

  it('review_mode_still_fails_a_fixture_body_string', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output(
      'chunk-ABC123.js',
      `export const label=${JSON.stringify(CORPUS_NAME)};const s="app-dev-sign-in";`,
    );

    const result = ws.run(['--allow-dev-sign-in']);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain(CORPUS_NAME);
  });

  it('review_mode_fails_when_the_sign_in_is_missing', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('main-ABC123.js', CLEAN_MAIN_JS);

    // A clean bundle with no sign-in needle is exactly what a production build looks like: the
    // review invocation must not accept it, because a reviewer could not open the form.
    const result = ws.run(['--allow-dev-sign-in']);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('the review output has no dev sign-in');
    expect(result.stderr).toContain('--allow-dev-sign-in');
  });

  it('review_mode_fails_when_only_the_sdk_route_remains', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('main-ABC123.js', CLEAN_MAIN_JS);
    await ws.output('chunk-SDK123.js', 'const url="/dev/session";');

    // The generated SDK always carries the route; only the mounted component proves the form.
    const result = ws.run(['--allow-dev-sign-in']);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('the review output has no dev sign-in');
  });

  it('review_mode_reports_the_sign_in_it_allowed', async () => {
    const ws = await workspace();
    await ws.fixture('permissions-list', corpusDocument(CORPUS_NAME));
    await ws.output('main-ABC123.js', CLEAN_MAIN_JS);
    await ws.output(
      'chunk-DEV123.js',
      'const s="app-dev-sign-in";const c="/v1/dev/session";const n="createDevSession";const l="Dev sign-in";',
    );

    const result = ws.run(['--allow-dev-sign-in']);

    expect(result.status).toBe(0);
    expect(result.stdout).toContain(`${FIXTURE_MARKERS.length} marker(s)`);
    expect(result.stdout).toContain('(dev sign-in allowed and present)');
  });
});
