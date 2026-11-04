// 09.1.1-T4 (tier T2): adversarial and boundary cases the shell suite does not cover (URL
// tampering, returnUrl, cross-org ids, later-phase routes, boot failures). Written by the QA agent.
//
// Every case asserts an INVARIANT (never a 403 where the answer must be 404, never a page error,
// never a redirect off-origin, never a feature page mounted behind a refusal), so the file is
// meaningful on the fixtures build and on any later bundle.
import { expect, test, type Page } from '@playwright/test';
import { chooseScope, expectScopeValue, scopeControl } from './scope-controls';

import {
  ANONYMOUS_SESSION,
  fixturesBaseUrl,
  LIMITED_GRANTS,
  LOCALES,
  localePath,
  mainText,
  navSegments,
  ORG_ACME,
  ORG_LUMEN,
  PROJECT_STOREFRONT,
  UNKNOWN_ORG,
  UNREADABLE_SESSION,
  withVariant,
  withVariants,
} from './support';

/** Every G1 entry of the registry: URL below `/orgs/:orgId` and the code its route requires. */
const G1_ENTRIES = [
  { segment: 'authoring', scope: 'project', permission: 'authoring:read' },
  { segment: 'generation', scope: 'project', permission: 'authoring:generate' },
  { segment: 'pending-changes', scope: 'project', permission: 'authoring:write' },
  { segment: 'overview', scope: 'project', permission: 'project:read' },
  { segment: 'ai-settings', scope: 'project', permission: 'project:read' },
  { segment: 'onboarding', scope: 'org', permission: 'project:create' },
  { segment: 'settings', scope: 'org', permission: 'organization:read' },
  { segment: 'members', scope: 'org', permission: 'membership:read' },
  { segment: 'usage', scope: 'org', permission: 'usage:read' },
  { segment: 'audit', scope: 'org', permission: 'audit:read' },
] as const;

/** `permissions-list` (default): tenant-assignable codes of the corpus. */
const DEFAULT_GRANTS = [
  'agent_pool:read',
  'agent_pool:write',
  'ai_draft:request',
  'artifact:read',
  'artifact:read_sensitive',
  'artifact:write',
  'audit:read',
  'authoring:generate',
  'authoring:read',
  'authoring:write',
  'case:read',
  'case:write',
  'ci_binding:write',
  'decision:read',
] as const;

/** `permissions-list-limited`. */
const LIMITED_CODES = [
  'audit:read',
  'authoring:read',
  'membership:read',
  'project:read',
  'role:read',
] as const;

/** Every later-phase segment (G2–G4) with the scope its registry row declares. */
const LATER_PHASE = [
  { segment: 'scripts', scope: 'project' },
  { segment: 'catalog', scope: 'project' },
  { segment: 'mapping', scope: 'project' },
  { segment: 'runs', scope: 'project' },
  { segment: 'failures', scope: 'project' },
  { segment: 'repairs', scope: 'project' },
  { segment: 'knowledge', scope: 'project' },
  { segment: 'availability', scope: 'project' },
  { segment: 'studio', scope: 'project' },
  { segment: 'suites', scope: 'project' },
  { segment: 'quality', scope: 'project' },
  { segment: 'releases', scope: 'project' },
  { segment: 'policies', scope: 'project' },
  { segment: 'deploy', scope: 'project' },
  { segment: 'integrations', scope: 'project' },
  { segment: 'secrets', scope: 'project' },
  { segment: 'security', scope: 'org' },
  { segment: 'support', scope: 'org' },
  { segment: 'agents', scope: 'org' },
  { segment: 'tokens', scope: 'org' },
] as const;

function entryPath(scope: 'org' | 'project', segment: string): string {
  return scope === 'org'
    ? `/orgs/${ORG_ACME}/${segment}`
    : `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/${segment}`;
}

/** Collects uncaught exceptions for the life of a page. */
function watchPageErrors(page: Page): Error[] {
  const errors: Error[] = [];
  page.on('pageerror', (error) => errors.push(error));
  return errors;
}

/** What the router decided, read back from the DOM alone (no app internals). */
interface Outcome {
  pathname: string;
  search: string;
  heading: string;
  forbidden: number;
  notFound: number;
  text: string;
}

async function outcome(page: Page): Promise<Outcome> {
  // A page owns its heading (the overview page renders its own `h1` too), but a tab body under the
  // Settings shell is a section, so its heading is an `h2` below the shell's `h1`. The same members
  // page mounts both ways (09.7.1-T2: the `members` sidebar entry and the Settings tab), so the
  // heading is whichever of the two the page rendered, in DOM order.
  const shellHeading = page.locator('#main-content h1');
  const bodyHeading = page.locator('app-members-page h2');
  const heading = (await shellHeading.or(bodyHeading).first().innerText()).trim();
  return {
    pathname: new URL(page.url()).pathname,
    search: new URL(page.url()).search,
    heading,
    forbidden: await page.locator('app-forbidden-pattern').count(),
    notFound: await page.locator('app-not-found-pattern').count(),
    text: await mainText(page),
  };
}

test.describe('AC4 — every guarded G1 route against both corpus grant sets', () => {
  for (const [label, codes, variant] of [
    ['default catalogue', DEFAULT_GRANTS, ''],
    ['limited catalogue', LIMITED_CODES, LIMITED_GRANTS],
  ] as const) {
    test(`the ${label} grants exactly the G1 routes whose code it holds`, async ({ page }) => {
      const errors = watchPageErrors(page);
      for (const entry of G1_ENTRIES) {
        const path = entryPath(entry.scope, entry.segment);
        await page.goto(
          variant === '' ? localePath('en', path) : withVariant(localePath('en', path), variant),
        );

        const rendered = await outcome(page);
        const granted = (codes as readonly string[]).includes(entry.permission);
        if (granted) {
          // Reached: the address is unchanged and neither pattern rendered.
          expect(rendered.pathname, `${entry.segment} (${entry.permission}) should match`).toBe(
            `/en${path}`,
          );
          expect(rendered.forbidden, `${entry.segment} rendered 403`).toBe(0);
          expect(rendered.notFound, `${entry.segment} rendered 404`).toBe(0);
          expect(rendered.heading.length, `${entry.segment} has no heading`).toBeGreaterThan(0);
        } else {
          // Refused: the 403 pattern, at /forbidden, with the attempt not echoed.
          expect(
            rendered.pathname,
            `${entry.segment} (${entry.permission}) should be refused`,
          ).toBe('/en/forbidden');
          expect(rendered.forbidden, `${entry.segment} did not render the 403 pattern`).toBe(1);
          expect(rendered.search, `${entry.segment} kept a query on the refusal`).toBe('');
          expect(rendered.text).not.toContain(path);
        }
      }
      expect(errors, 'no uncaught error during the matrix').toEqual([]);
    });
  }

  test('the refusal is the same page whether the code is missing from a full or a trimmed set', async ({
    page,
  }) => {
    // `usage:read` is missing from BOTH corpora; `organization:read` from both too. A guard that
    // keyed on set size rather than the code would still pass the matrix above and fail here.
    const seen: string[] = [];
    for (const variant of ['', LIMITED_GRANTS]) {
      const path = localePath('en', `/orgs/${ORG_ACME}/usage`);
      await page.goto(variant === '' ? path : withVariant(path, variant));
      const rendered = await outcome(page);
      expect(rendered.pathname).toBe('/en/forbidden');
      seen.push(rendered.text);
    }
    expect(new Set(seen).size).toBe(1);
  });
});

test.describe('AC4 — a listed project id under the wrong organization', () => {
  test('a project of org A opened under org B is the 404 pattern, never 403', async ({ page }) => {
    // The corpus lists org 2 but only org 1's projects; project STOREFRONT carries org_id = org 1.
    // This is the `candidate.org_id === orgId` check, which the lane's e2e never exercises (it only
    // tries a project id that exists nowhere).
    const path = localePath('en', `/orgs/${ORG_LUMEN}/projects/${PROJECT_STOREFRONT}/overview`);
    await page.goto(withVariant(path, LIMITED_GRANTS));

    const rendered = await outcome(page);
    expect(rendered.forbidden).toBe(0);
    expect(rendered.notFound).toBe(1);
    expect(rendered.heading).toBe('Not found');
    // The address is kept: the wildcard rendered in place, no redirect, no fallback.
    expect(rendered.pathname).toBe(`/en/orgs/${ORG_LUMEN}/projects/${PROJECT_STOREFRONT}/overview`);
    // And the feature page never mounted.
    expect(await page.locator('app-project-overview').count()).toBe(0);
    // The copy is the unknown-path copy.
    await page.goto(localePath('en', `/orgs/${UNKNOWN_ORG}`));
    const unknown = await outcome(page);
    expect(rendered.text).toBe(unknown.text);
  });
});

test.describe('AC4 — a later-phase entry typed by URL, every segment, both scopes', () => {
  test('all 20 later-phase routes are the same 404, with either grant set', async ({ page }) => {
    const errors = watchPageErrors(page);
    const rendered: Outcome[] = [];
    for (const variant of ['', LIMITED_GRANTS]) {
      for (const entry of LATER_PHASE) {
        const path = entryPath(entry.scope, entry.segment);
        await page.goto(
          variant === '' ? localePath('en', path) : withVariant(localePath('en', path), variant),
        );
        const result = await outcome(page);
        // The phase guard runs BEFORE the permission guard, so the answer is 404 (not matched),
        // never the 403 redirect a matched-but-unauthorised route would give.
        expect(result.forbidden, `${entry.segment} rendered 403`).toBe(0);
        expect(result.notFound, `${entry.segment} rendered no 404 pattern`).toBe(1);
        expect(result.pathname, `${entry.segment} was redirected`).toBe(`/en${path}`);
        rendered.push(result);
      }
    }
    expect(new Set(rendered.map((entry) => entry.text)).size).toBe(1);
    expect(new Set(rendered.map((entry) => entry.heading)).size).toBe(1);
    expect(errors).toEqual([]);
  });

  test('the sidebar never lists a later-phase segment, in either locale or grant set', async ({
    page,
  }) => {
    for (const locale of LOCALES) {
      for (const variant of ['', LIMITED_GRANTS]) {
        const path = localePath(locale, entryPath('project', 'authoring'));
        await page.goto(variant === '' ? path : withVariant(path, variant));
        await expect(page.locator('app-shell-nav nav a').first()).toBeVisible();
        const visible = await navSegments(page);
        expect(visible.filter((s) => LATER_PHASE.some((e) => e.segment === s))).toEqual([]);
      }
    }
  });
});

test.describe('session state vs. route state', () => {
  test('an anonymous deep link to a later-phase route goes to login, with the path kept', async ({
    page,
  }) => {
    // The auth guard is the outermost matcher, so "no session" wins over "phase hidden": a
    // signed-out visitor is asked to sign in rather than shown a 404.
    const deep = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs`;
    await page.goto(
      withVariant(localePath('en', deep), '?fixture.variant.listMyOrganizations=unauthenticated'),
    );
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    expect(new URL(page.url()).searchParams.get('returnUrl')).toBe(deep);
  });

  test('an anonymous visitor of a pattern page gets no workspace', async ({ page }) => {
    for (const pattern of ['forbidden', 'welcome', 'unavailable']) {
      await page.goto(
        withVariant(
          localePath('en', `/${pattern}`),
          '?fixture.variant.listMyOrganizations=unauthenticated',
        ),
      );
      // Pattern pages are unguarded shell routes; the invariant is that no session fact renders.
      await expect(scopeControl(page, 0)).toBeDisabled();
      expect(await navSegments(page), `${pattern} showed navigation`).toEqual([]);
      expect(await mainText(page)).not.toContain('Storefront web');
    }
  });

  test('an unknown fixture variant fails closed instead of showing an empty workspace', async ({
    page,
  }) => {
    // The backend answers 501 `fixture_not_found`; the store must read that as "could not read".
    for (const query of [
      '?fixture.variant.listMyOrganizations=bogus',
      '?fixture.variant.listPermissions=bogus',
    ]) {
      await page.goto(withVariant(localePath('en', '/'), query));
      await expect.poll(() => new URL(page.url()).pathname).toBe('/en/unavailable');
      await expect(page.locator('#main-content h1')).toHaveText('Workspace unavailable');
      expect(await page.locator('app-shell-nav nav a').count()).toBe(0);
    }
  });
});

test.describe('returnUrl is never an open redirect', () => {
  const attempts = [
    '//evil.example',
    'https://evil.example/steal',
    'http://evil.example',
    'javascript:document.title="pwned"',
    '\\\\evil.example',
    '/%2F%2Fevil.example',
    '/\\evil.example',
    'data:text/html,<script>document.title="pwned"</script>',
  ];

  for (const attempt of attempts) {
    test(`/login?returnUrl=${attempt} stays put`, async ({ page }) => {
      const appHost = new URL(fixturesBaseUrl()).host;
      const offOrigin: string[] = [];
      page.on('request', (request) => {
        const url = new URL(request.url());
        if (url.host !== appHost) offOrigin.push(url.href);
      });
      const errors = watchPageErrors(page);
      // Anonymous, so the login page stays on screen: an authenticated tab is redirected through
      // the same validator (that half is `auth.spec.ts` "a tampered returnUrl sends the visitor
      // home"), and neither path may reflect the value.
      await page.goto(
        withVariants(localePath('en', '/login'), [
          ANONYMOUS_SESSION,
          `?returnUrl=${encodeURIComponent(attempt)}`,
        ]),
      );

      // Still the sign-in card, same origin, nothing executed.
      await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');
      expect(new URL(page.url()).pathname).toBe('/en/login');
      expect(new URL(page.url()).host).not.toContain('evil');
      expect(await page.title()).not.toBe('pwned');
      expect(offOrigin).toEqual([]);
      expect(errors).toEqual([]);
      // The parameter is not reflected as a link target or as markup.
      const hrefs = await page
        .locator('a')
        .evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
      expect(
        hrefs.filter((href) => href.includes('evil') || href.startsWith('javascript')),
      ).toEqual([]);
      expect(await page.locator('main').innerText()).not.toContain('evil.example');
    });
  }
});

test.describe('scope switching under load', () => {
  test('two rapid organization switches settle on the last choice, with no stale scope', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        localePath('en', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`),
        `${LIMITED_GRANTS}&fixture.latency=400`,
      ),
    );
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    const orgSelect = scopeControl(page, 0);
    await expect(orgSelect).toBeEnabled();
    // Two changes back to back: the second must win, and the first navigation's scope must not
    // land later and re-point the URL or the navigation links at org 2.
    await chooseScope(page, orgSelect, ORG_LUMEN);
    await chooseScope(page, orgSelect, ORG_ACME);

    await expect
      .poll(() => new URL(page.url()).pathname, { timeout: 15_000 })
      .toBe(`/en/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`);
    // Let any late answer of the abandoned navigation arrive.
    await page.waitForTimeout(1_200);

    expect(new URL(page.url()).pathname).toBe(
      `/en/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`,
    );
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    await expectScopeValue(orgSelect, ORG_ACME);
    const hrefs = await page
      .locator('app-shell-nav nav a')
      .evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
    expect(hrefs.length).toBeGreaterThan(0);
    expect(hrefs.filter((href) => href.includes(ORG_LUMEN))).toEqual([]);
  });

  test('a slow project list does not keep the previous organization’s navigation on screen', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        localePath('en', `/orgs/${ORG_ACME}/audit`),
        `${LIMITED_GRANTS}&fixture.latency=600`,
      ),
    );
    await expect(page.locator('#main-content h1')).toBeVisible();

    const orgSelect = scopeControl(page, 0);
    await chooseScope(page, orgSelect, ORG_LUMEN);
    // While the project list is still in flight, the store must already have dropped org 1's scope.
    await page.waitForTimeout(150);
    const href = await page.locator('app-shell-nav nav a').first().getAttribute('href');
    expect(href ?? '').not.toContain(ORG_ACME);
  });
});

test.describe('history after a guard redirect', () => {
  test('Back from /forbidden returns to the previous page, not into a redirect loop', async ({
    page,
  }) => {
    const overview = withVariant(
      localePath('en', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`),
      LIMITED_GRANTS,
    );
    await page.goto(overview);
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    // `settings` needs `organization:read`, which the limited set lacks.
    await page.goto(withVariant(localePath('en', `/orgs/${ORG_ACME}/settings`), LIMITED_GRANTS));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/forbidden');

    await page.goBack();
    await expect
      .poll(() => new URL(page.url()).pathname, { timeout: 10_000 })
      .toBe(`/en/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`);
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    await page.goForward();
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/forbidden');
  });
});

test.describe('URL tampering', () => {
  type Verdict = 'not-found' | 'resolved' | 'harness-rejected';
  const cases: { name: string; path: string; expect: Verdict }[] = [
    { name: 'encoded slash in the org id', path: `/orgs/${ORG_ACME}%2Fextra`, expect: 'not-found' },
    { name: 'double-encoded slash', path: `/orgs/${ORG_ACME}%252Fextra`, expect: 'not-found' },
    // The static server decodes the path and refuses `..` before Angular ever sees it (403), and
    // refuses a malformed escape with 400: the app is not reached, so this is a harness verdict,
    // recorded rather than asserted away.
    {
      name: 'encoded dot-dot',
      path: `/orgs/${ORG_ACME}%2F..%2Fforbidden`,
      expect: 'harness-rejected',
    },
    { name: 'uppercase uuid', path: `/orgs/${ORG_ACME.toUpperCase()}`, expect: 'not-found' },
    { name: 'a 4 kB org id', path: `/orgs/${'a'.repeat(4096)}`, expect: 'not-found' },
    // The URL parser itself collapses the dot before any request, so Angular receives `/en/orgs`,
    // which matches nothing: the value of this case is that a dot segment cannot escape into a
    // route (observed 404 in place). It does not exercise a dot inside an id — `..`, `.`,
    // `o-1%2Fextra` and `%6F-1` are covered directly in the unit spec qa-scope-hardening.spec.ts.
    { name: 'a lone dot segment', path: `/orgs/.`, expect: 'not-found' },
    { name: 'a percent sign only', path: `/orgs/%`, expect: 'harness-rejected' },
    { name: 'trailing slash after the org', path: `/orgs/${ORG_ACME}/`, expect: 'resolved' },
    { name: 'matrix param on the org id', path: `/orgs/${ORG_ACME};x=1`, expect: 'resolved' },
    {
      name: 'matrix param on a real route',
      path: `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/authoring;evil=1`,
      expect: 'resolved',
    },
  ];

  for (const item of cases) {
    test(`${item.name} is handled without a 403 pattern and without an exception`, async ({
      page,
    }) => {
      const errors = watchPageErrors(page);
      const response = await page.goto(withVariant(localePath('en', item.path), LIMITED_GRANTS));
      const status = response?.status() ?? 0;

      if (item.expect === 'harness-rejected') {
        // The SPA fallback never answered: the static server refused the raw path shape.
        expect([400, 403], `harness answered ${status}`).toContain(status);
        expect(await page.locator('app-shell').count()).toBe(0);
        expect(errors).toEqual([]);
        console.log(`QA-HARNESS-REJECT ${item.name}: ${status}`);
        return;
      }

      const rendered = await outcome(page);
      console.log(
        `QA-TAMPER ${item.name} -> ${rendered.pathname}${rendered.search} forbidden=${rendered.forbidden} notFound=${rendered.notFound} h1=${JSON.stringify(rendered.heading)}`,
      );
      expect(status).toBe(200);
      expect(rendered.forbidden, 'a tampered URL confirmed existence with a 403').toBe(0);
      expect(errors, 'a tampered URL threw').toEqual([]);
      if (item.expect === 'not-found') {
        expect(rendered.notFound, `${item.path} should be 404`).toBe(1);
        expect(rendered.heading).toBe('Not found');
      } else {
        // Resolved: a real page, not a pattern.
        expect(rendered.notFound).toBe(0);
        expect(rendered.heading.length).toBeGreaterThan(0);
        expect(rendered.pathname.startsWith('/en/orgs/')).toBe(true);
      }
    });
  }

  test('a resolved URL with junk segments cannot smuggle another org into the navigation', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        localePath('en', `/orgs/${ORG_ACME};x=1/projects/${PROJECT_STOREFRONT}/authoring`),
        LIMITED_GRANTS,
      ),
    );
    await expect(page.locator('#main-content h1')).toBeVisible();
    const hrefs = await page
      .locator('app-shell-nav nav a')
      .evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
    expect(hrefs.filter((href) => !href.startsWith(`/en/orgs/${ORG_ACME}/`))).toEqual([]);
  });
});

test.describe('an entry typed at the wrong scope', () => {
  // The registry decides the scope of an entry (an org-scope row lives at
  // `/orgs/:orgId/<segment>`), and the route table only mounts it there. A URL that puts a
  // project-scope entry at org scope (or the reverse) must not fall through to a read: it is the
  // 404 pattern, not a 403 (the entry exists and may even be granted).
  const wrongScope = [
    // project-scope entries at org scope, both of whose codes this session holds
    `/orgs/${ORG_ACME}/authoring`,
    `/orgs/${ORG_ACME}/overview`,
    // org-scope entries at project scope, one granted (`audit:read`) and one not
    `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/audit`,
    `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/settings`,
  ];

  for (const path of wrongScope) {
    test(`${path} is the 404 pattern, never 403`, async ({ page }) => {
      const errors = watchPageErrors(page);
      await page.goto(withVariant(localePath('en', path), LIMITED_GRANTS));
      const rendered = await outcome(page);
      expect(rendered.forbidden, 'wrong-scope URL rendered 403').toBe(0);
      expect(rendered.notFound, 'wrong-scope URL did not render the 404 pattern').toBe(1);
      expect(rendered.heading).toBe('Not found');
      // In place: no redirect to a landing address.
      expect(rendered.pathname).toBe(`/en${path}`);
      expect(errors).toEqual([]);
    });
  }
});

test.describe('the unavailable page’s retry', () => {
  test('a retry that still cannot read says so in place and keeps the control', async ({
    page,
  }) => {
    await page.goto(withVariant(localePath('en', '/'), UNREADABLE_SESSION));
    await expect(page.locator('#main-content h1')).toHaveText('Workspace unavailable');
    const retry = page.locator('#main-content button');
    await expect(page.locator('#main-content [role="status"]')).toHaveText('');

    // The fixture toggles are held in memory from the boot URL, so this retry reads the same 503:
    // the page must announce the failure instead of navigating and bouncing straight back.
    await retry.click();
    await expect(page.locator('#main-content [role="status"]')).toHaveText(
      'The workspace is still unavailable. Try again later.',
    );
    await expect(retry).toBeEnabled();
    await expect(retry).toHaveText('Try again');
    await expect(page.locator('#main-content section')).toHaveAttribute('aria-busy', 'false');
    expect(new URL(page.url()).pathname).toBe('/en/unavailable');
  });

  test('a reload of /unavailable reads no session; Try again recovers when the read works', async ({
    page,
  }) => {
    await page.goto(withVariant(localePath('en', '/'), UNREADABLE_SESSION));
    await expect(page.locator('#main-content h1')).toHaveText('Workspace unavailable');
    await page.reload();
    await expect(page.locator('#main-content h1')).toHaveText('Workspace unavailable');

    // No guard runs on a pattern page, so the reload read nothing: no scope, no navigation.
    expect(await navSegments(page)).toEqual([]);
    await expect(scopeControl(page, 0)).toBeDisabled();

    // The reloaded document carries no toggles, so its retry reads the healthy corpus and the
    // page leaves the failure state for a real route. (Under the default corpus that landing then
    // reaches a route its grant set lacks, so the 403 pattern is the expected destination — what
    // matters here is that the retry really re-read the session and navigated.)
    await page.locator('#main-content button').click();
    await expect.poll(() => new URL(page.url()).pathname).not.toBe('/en/unavailable');
    await expect(page.locator('app-service-unavailable-page')).toHaveCount(0);
    await expect(page.locator('#main-content h1')).not.toHaveText('Workspace unavailable');
  });
});

test.describe('theme storage edge cases', () => {
  for (const stored of ['purple', 'DARK', ' dark', '']) {
    test(`a corrupt stored choice ${JSON.stringify(stored)} leaves data-theme unset`, async ({
      page,
    }) => {
      await page.addInitScript((value) => {
        localStorage.setItem('evidgate-theme', value);
      }, stored);
      await page.emulateMedia({ colorScheme: 'light' });
      await page.goto(withVariant(localePath('en', `/orgs/${ORG_ACME}/audit`), LIMITED_GRANTS));
      await expect(page.locator('#main-content h1')).toBeVisible();

      expect(await page.evaluate(() => document.documentElement.getAttribute('data-theme'))).toBe(
        null,
      );
      const toggle = page.locator('button.theme-toggle');
      await expect(toggle).toHaveAttribute('aria-pressed', 'false');
      // The control still works and repairs the value.
      await toggle.click();
      expect(await page.evaluate(() => localStorage.getItem('evidgate-theme'))).toBe('dark');
      expect(await page.evaluate(() => document.documentElement.getAttribute('data-theme'))).toBe(
        'dark',
      );
    });
  }
});

test.describe('mobile drawer edges', () => {
  test.use({ viewport: { width: 360, height: 720 } });

  test('the close button closes the full-width drawer at 360px', async ({ page }) => {
    await page.goto(withVariant(localePath('en', `/orgs/${ORG_ACME}/audit`), LIMITED_GRANTS));
    const toggle = page.locator('button.nav-toggle');
    await toggle.click();
    await expect(page.locator('aside.navigation')).toHaveAttribute('aria-modal', 'true');
    await page.locator('button.close-button').click();
    await expect(page.locator('aside.navigation')).not.toHaveAttribute('aria-modal', 'true');
    await expect(toggle).toBeFocused();
  });

  test('the exposed scrim closes the drawer at 600px and focus returns to the opener', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 600, height: 720 });
    await page.goto(withVariant(localePath('en', `/orgs/${ORG_ACME}/audit`), LIMITED_GRANTS));
    const toggle = page.locator('button.nav-toggle');
    await toggle.click();
    await expect(page.locator('aside.navigation')).toHaveAttribute('aria-modal', 'true');

    await page.locator('button.scrim').click({ position: { x: 590, y: 640 } });
    await expect(page.locator('aside.navigation')).not.toHaveAttribute('aria-modal', 'true');
    await expect(toggle).toBeFocused();
  });

  test('a viewport change to desktop closes and un-inerts the navigation', async ({ page }) => {
    await page.goto(withVariant(localePath('en', `/orgs/${ORG_ACME}/audit`), LIMITED_GRANTS));
    const toggle = page.locator('button.nav-toggle');
    await toggle.click();
    await expect(page.locator('aside.navigation')).toHaveAttribute('aria-modal', 'true');

    await page.setViewportSize({ width: 1280, height: 800 });
    const aside = page.locator('aside.navigation');
    await expect(aside).not.toHaveAttribute('aria-modal', 'true');
    await expect(aside).not.toHaveAttribute('inert', '');
    // The drawer cannot be left "open" invisibly: the main stack is no longer inert.
    await expect(page.locator('.main-stack')).not.toHaveAttribute('inert', '');
    // And the same control now collapses the rail instead of opening a dialog.
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });
});
