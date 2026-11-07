// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1, §2 AC5; D-S98-Q2/Q3): the sign-in round trip in a real
// browser, against the FIXTURES build.
//
// The API is not running here, so the fixture IdP stands in for it at the one seam that matters: a
// `page.route` on `/v1/auth/oidc/**` records the start URL the app asked for and answers `302` with
// the `returnUrl` it carried, exactly as `contracts/openapi.yaml` has the real start route answer
// once the provider has authenticated the visitor. Everything after that redirect is the app: the
// callback page reloads the session through the fixture backend (which is authenticated by default)
// and navigates to the validated target.
//
// This suite lives under `e2e/shell/**`, which `playwright.config.ts` excludes and
// `playwright.fixtures.config.ts` owns — a login flow cannot be exercised against the production
// bundle, because the fixture backend and the `/auth/callback` landing it needs are a `fixtures`
// configuration replacement.
import type { Page, Route } from '@playwright/test';
import { expect, test } from '@playwright/test';

import {
  ANONYMOUS_SESSION,
  fixturesBaseUrl,
  LIMITED_GRANTS,
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariant,
} from './support';

const DEEP_LINK = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`;

// A sign-in round trip lands in a NEW document, so the fixture variants of the page the visitor was
// refused do not survive it: the callback URL is built by the app and carries only `returnUrl`. The
// `overview` entry demands `project:read` (`shell/nav/nav-registry.ts:106`), which the corpus's
// DEFAULT permission catalogue does not hold — only the grants-limited one does. The fixture IdP
// therefore answers the start route with the callback URL PLUS the grants seed, exactly the way
// `LIMITED_GRANTS` seeds a page the suite navigates to directly.
const GRANT_SEED = LIMITED_GRANTS.slice(1);

/** The API's session read, expired after the first one succeeded (AC5 mid-navigation half). */
const EXPIRED_SCOPE = '?fixture.variant.listProjects=unauthenticated';

// The expiry case deep-links to the route PLUS the variant that makes the scope read expire: the
// attempted URL is the whole URL the visitor asked for, query included, so that is what the store
// must put in `returnUrl` (a filter or a tab must survive the round trip just as the path does).
const EXPIRED_ATTEMPT = `${DEEP_LINK}${EXPIRED_SCOPE}`;

interface StartCall {
  readonly url: URL;
  /** The `returnUrl` this start URL asked the API to come back to, already decoded once. */
  readonly callback: URL | null;
}

/**
 * Answers every provider start with the `302` the real API sends once the provider is done: a
 * navigation straight to the `returnUrl` the app passed, which for this app is its own
 * `/auth/callback?returnUrl=<target>`.
 */
async function stubProviderIdp(page: Page): Promise<StartCall[]> {
  const calls: StartCall[] = [];
  await page.route('**/v1/auth/oidc/**', async (route: Route) => {
    const url = new URL(route.request().url());
    const returnUrl = url.searchParams.get('returnUrl');
    calls.push({ url, callback: returnUrl === null ? null : absolute(returnUrl, url) });
    await route.fulfill({
      status: 302,
      // `returnUrl` is percent-encoded, so appended parameters cannot be swallowed by it.
      headers: {
        location: returnUrl === null ? '/' : `${returnUrl}&${GRANT_SEED}`,
      },
    });
  });
  return calls;
}

function absolute(path: string, base: URL): URL | null {
  try {
    return new URL(path, base);
  } catch {
    return null;
  }
}

/** The provider button the login page renders for a human, by its label. */
function providerButton(page: Page, provider: 'Google' | 'GitHub') {
  return page.getByRole('button', { name: `Continue with ${provider}` });
}

test.describe('sign-in (AC5, T5.1)', () => {
  test('a click leaves for the API start route and returns to the attempted route', async ({
    page,
  }) => {
    const starts = await stubProviderIdp(page);

    // 1. An anonymous deep link becomes `/login` with the attempted path (T4 behaviour, kept).
    await page.goto(withVariant(localePath('en', DEEP_LINK), ANONYMOUS_SESSION));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    expect(new URL(page.url()).searchParams.get('returnUrl')).toBe(DEEP_LINK);

    // 2. The page offers the two providers and no password field: the provider account is the only
    //    credential (contracts/openapi.yaml has no password input on any auth path).
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');
    await expect(providerButton(page, 'Google')).toBeEnabled();
    await expect(providerButton(page, 'GitHub')).toBeEnabled();
    await expect(page.locator('main input')).toHaveCount(0);

    // 3. One click is one full-page navigation to the API, with the callback as the return target.
    await providerButton(page, 'Google').click();
    await expect.poll(() => starts.length).toBe(1);

    const start = starts[0]!;
    expect(start.url.pathname).toBe('/v1/auth/oidc/google/start');
    expect(start.callback?.pathname).toBe('/en/auth/callback');
    // The inner target is the route the visitor was refused, so the round trip ends where it began.
    expect(start.callback?.searchParams.get('returnUrl')).toBe(DEEP_LINK);

    // 4. The landing reads only `returnUrl`; the OAuth parameters never reach it (the stub answers
    //    with the returnUrl alone, and the page must not need anything else).
    await page.waitForURL('**/auth/callback?**');
    const landing = new URL(page.url()).searchParams;
    // The page is handed `returnUrl` and (in this build) the grants seed of the fixture IdP; the
    // OAuth parameters are absent, and the page itself reads only the one it needs.
    expect(landing.has('returnUrl')).toBe(true);
    expect(landing.has('code')).toBe(false);
    expect(landing.has('state')).toBe(false);
    await expect(providerButton(page, 'Google')).toHaveCount(0);

    // 5. Back on the original route, inside the shell, with no OAuth parameter in sight.
    await expect.poll(() => new URL(page.url()).pathname).toBe(`/en${DEEP_LINK}`);
    expect(new URL(page.url()).searchParams.has('code')).toBe(false);
    expect(new URL(page.url()).searchParams.has('state')).toBe(false);
    await expect(page.locator('app-shell')).toHaveCount(1);

    // 6. Nothing about the session was written to web storage (T38: cookie only).
    expect(await webStorageEntries(page)).toEqual({ local: [], session: [] });
  });

  test('a tampered returnUrl sends the visitor home instead of off the site', async ({ page }) => {
    const starts = await stubProviderIdp(page);
    const appHost = new URL(fixturesBaseUrl()).host;
    const offOrigin: string[] = [];
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.host !== appHost) {
        offOrigin.push(url.href);
      }
    });

    // `/login?returnUrl=//evil.test` — the value an attacker would bookmark or link.
    await page.goto(
      `${withVariant(localePath('en', '/login'), ANONYMOUS_SESSION)}&returnUrl=${encodeURIComponent('//evil.test')}`,
    );
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');

    await providerButton(page, 'Google').click();
    await expect.poll(() => starts.length).toBe(1);

    // The rejected value never leaves the tab: the callback is asked to return to the workspace root.
    expect(starts[0]!.callback?.searchParams.get('returnUrl')).toBe('/');

    // The tampered value became '/', and '/' is this app's own root: the landing guard resolves the
    // first org/project from the server's lists and pushes the `overview` entry — never evil.test,
    // and never a foreign origin.
    await expect.poll(() => new URL(page.url()).pathname).toBe(`/en${DEEP_LINK}`);
    expect(page.url()).not.toContain('evil');
    expect(offOrigin).toEqual([]);
    await expect(page.locator('app-shell')).toHaveCount(1);
  });

  test('a signed-out tab is told it signed out on this tab, and stays where it is', async ({
    page,
  }) => {
    // The visit is anonymous, so `/login` stays on screen: an authenticated tab is redirected away
    // by its own guard, which is the behaviour test 1 relies on.
    await page.goto(
      `${withVariant(localePath('en', '/login'), ANONYMOUS_SESSION)}&reason=signed-out`,
    );

    // The words are the tab's, not the server's: the HttpOnly cookie is still valid until
    // 02.3.2-T2 lands (session-ender.ts), so nothing here may claim the session ended.
    await expect(page.locator('#login-heading')).toHaveText('Sign in to Evidgate');
    await expect(page.locator('.login-notice')).toContainText('Signed out on this tab');
    await expect(page.locator('.login-notice')).not.toContainText('session ended');
    await expect(providerButton(page, 'GitHub')).toBeEnabled();
    // A signed-out tab stays on the login page: no self-redirect, no second probe, and no trace in
    // storage that a later sign-in could pick up. (The `/v1` reads of this build are answered by the
    // in-process fixture backend, so they are not observable as network requests — the store-level
    // "no request after a sign-out" claim is proven in `session.store.boundary.spec.ts`, and this case proves
    // the page it lands on is stable.)
    // Condition-based, no fixed sleep: the banner is up, both controls are idle and the URL has not
    // moved under them. A self-redirect would change the path and fail these locator asserts.
    await expect(page.locator('.login-notice')).toBeVisible();
    await expect(page.locator('#login-heading')).toBeVisible();
    await expect(page).toHaveURL(/\/en\/login\?/);
    expect(new URL(page.url()).pathname).toBe('/en/login');
    expect(await webStorageEntries(page)).toEqual({ local: [], session: [] });
  });

  test('an expiry mid-navigation returns to the original route after signing in', async ({
    page,
  }) => {
    const starts = await stubProviderIdp(page);

    // The session is valid for the first read (orgs) and expired for the scope read (projects).
    await page.goto(`${localePath('en', DEEP_LINK)}${EXPIRED_SCOPE}`);
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');

    const login = new URL(page.url());
    expect(login.searchParams.get('reason')).toBe('expired');
    // The attempted URL, in full: path and query, taken from the navigation the guard interrupted
    // (not from the guard's own partial segments, which named the org only).
    expect(login.searchParams.get('returnUrl')).toBe(EXPIRED_ATTEMPT);
    await expect(page.locator('.login-notice')).toContainText('Your session expired');

    await providerButton(page, 'Google').click();
    await expect.poll(() => starts.length).toBe(1);
    expect(starts[0]!.callback?.searchParams.get('returnUrl')).toBe(EXPIRED_ATTEMPT);

    // The second document has no fixture variant, so the session is valid again and the round trip
    // ends where the expiry interrupted it.
    await expect.poll(() => new URL(page.url()).pathname).toBe(`/en${DEEP_LINK}`);
    await expect(page.locator('app-shell')).toHaveCount(1);
  });
});

/** The non-auth storage a document holds, so a spec can prove nothing was put there. */
async function webStorageEntries(page: Page): Promise<{ local: string[]; session: string[] }> {
  return page.evaluate(() => ({
    local: Object.keys(localStorage).sort(),
    session: Object.keys(sessionStorage).sort(),
  }));
}
