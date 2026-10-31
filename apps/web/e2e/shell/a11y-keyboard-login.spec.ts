// 09.8.1-T1.2 (plan 09.8.1 §10 T1, story AC2; docs/a11y/parts/login.md): the keyboard-only
// walkthrough of the sign-in flow, and the two focus findings it pins — F-LOGIN-01 (every
// client-side entry into `/login` left focus on `body`) and F-LOGIN-02 (activating a provider
// disabled the button that held focus, leaving the still-rendered page with no focus at all).
//
// Every interaction here is `page.keyboard`: no `click()`, no `{ force: true }`, so the path this
// suite describes is one a keyboard or switch visitor can actually walk. Focus is asserted by
// ELEMENT IDENTITY (`toBeFocused` on a named locator, or the `activeElement` id/tag) and never by a
// Tab count alone. The provider ring is asserted from the computed style, because a focus target
// nobody can see is the failure the finding names.
//
// This spec lives under `e2e/shell/**`, which only `playwright.fixtures.config.ts` runs: the sign-in
// round trip needs the fixture IdP and the fixture backend (see `auth.spec.ts` for the same seam).
import type { Page, Route } from '@playwright/test';
import { expect, test } from '@playwright/test';

import {
  activeElement,
  ANONYMOUS_SESSION,
  LIMITED_GRANTS,
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  withVariant,
} from './support';

const DEEP_LINK = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`;

/** The session read answers `401` after the first success: the scope read expires on the way in. */
const EXPIRED_SCOPE = '?fixture.variant.listProjects=unauthenticated';

// A sign-in round trip lands in a NEW document, which drops the fixture variants of the page the
// visitor was refused; the callback URL is built by the app and carries only `returnUrl`. The
// `overview` route demands `project:read`, which only the grants-limited catalogue holds, so the
// stub answers the start route with the callback URL plus the missing seed (as `auth.spec.ts` does).
const GRANT_SEED = LIMITED_GRANTS.slice(1);

/** One provider start the app asked for: the URL, and the `returnUrl` it carried (still encoded). */
interface StartCall {
  readonly url: URL;
  readonly callback: string | null;
}

/** The provider button the login page renders for a human, by its label. */
function providerButton(page: Page, provider: 'Google' | 'GitHub') {
  return page.getByRole('button', { name: `Continue with ${provider}` });
}

/**
 * The fixture IdP, at the one seam that matters (`auth.spec.ts` uses the same one).
 *
 * `land` answers the `302` the real API sends once the provider has authenticated the visitor: the
 * bottom half of the round trip, where this app's callback page takes over.
 *
 * `stay` aborts the request, which a browser does NOT commit as a new document, so the sign-in page
 * stays exactly as the app left it — providers disabled, its own focus target holding the ring. That is
 * the state F-LOGIN-02 is about; a real `302` would leave the page instead, and Playwright waits for
 * that navigation rather than showing it.
 */
async function stubProviderIdp(page: Page, answer: 'land' | 'stay'): Promise<StartCall[]> {
  const calls: StartCall[] = [];
  await page.route('**/v1/auth/oidc/**', async (route: Route) => {
    const url = new URL(route.request().url());
    const returnUrl = url.searchParams.get('returnUrl');
    calls.push({ url, callback: returnUrl });
    if (answer === 'stay') {
      await route.abort('aborted');
      return;
    }
    await route.fulfill({
      status: 302,
      // `returnUrl` is percent-encoded, so appended parameters cannot be swallowed by it.
      headers: { location: returnUrl === null ? '/' : `${returnUrl}&${GRANT_SEED}` },
    });
  });
  return calls;
}

test.describe('sign-in, keyboard only (AC2, T1.2)', () => {
  test('a page load keeps the browser’s focus start, and the first Tab is the first provider', async ({
    page,
  }) => {
    await page.goto(withVariant(localePath('en', '/login'), ANONYMOUS_SESSION));
    await expect(page.getByRole('main', { name: 'Sign in to Evidgate' })).toBeVisible();

    // `/login` renders outside the shell, so there is no sidebar, no skip link and no `#main-content`.
    await expect(page.locator('app-shell, #main-content')).toHaveCount(0);
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');
    await expect(page.getByRole('status')).toHaveText('');

    // F-LOGIN-01: a fresh DOCUMENT load is the one entry that keeps the browser's own focus start —
    // there is no earlier page whose focus could have been lost, and the heading is a programmatic
    // target (`tabindex="-1"`), never a Tab stop of its own.
    expect(await activeElement(page)).toMatchObject({ tag: 'body' });
    await expect(page.locator('#login-heading')).toHaveAttribute('tabindex', '-1');

    // The first Tab reaches the first provider: nothing focusable precedes it.
    await page.keyboard.press('Tab');
    const google = providerButton(page, 'Google');
    await expect(google).toBeFocused();

    // The ring is the shared focus style, not a hover or a light-only effect.
    await expect(google).toHaveCSS('outline-style', 'solid');
    await expect(google).toHaveCSS('outline-width', '2px');
    await expect(google).toHaveCSS('outline-offset', '2px');
    await expect(google).toHaveCSS('outline-color', 'rgb(0, 107, 102)');
  });

  test('the provider ring follows the stored dark theme', async ({ page }) => {
    // Seeded before the app bootstraps, the way a returning visitor's storage already holds it.
    await seedStoredTheme(page, 'dark');
    await page.goto(withVariant(localePath('en', '/login'), ANONYMOUS_SESSION));
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');

    await page.keyboard.press('Tab');
    const google = providerButton(page, 'Google');
    await expect(google).toBeFocused();
    await expect(google).toHaveCSS('outline-color', 'rgb(118, 216, 203)');
  });

  test('Tab and Shift+Tab walk the two providers and then leave the page', async ({ page }) => {
    await page.goto(withVariant(localePath('en', '/login'), ANONYMOUS_SESSION));
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');

    const google = providerButton(page, 'Google');
    const github = providerButton(page, 'GitHub');

    await page.keyboard.press('Tab');
    await expect(google).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(github).toBeFocused();

    // Reverse order holds: both providers are ordinary native buttons in document order.
    await page.keyboard.press('Shift+Tab');
    await expect(google).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(github).toBeFocused();

    // There is no third tab stop: the page offers exactly the two providers and no link, and Tab
    // past the last one leaves the document instead of landing on something invisible.
    await expect(page.locator('main a, main input, main [tabindex="0"]')).toHaveCount(0);
    await expect(page.getByRole('button')).toHaveCount(2);
    await page.keyboard.press('Tab');
    expect(await activeElement(page)).toMatchObject({ tag: 'body' });

    // No dialog or overlay exists here, so Escape neither moves focus nor navigates.
    const before = page.url();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(page.url()).toBe(before);
    expect(await activeElement(page)).toMatchObject({ tag: 'body' });

    // Going back to the first provider is one Shift+Tab from inside the page.
    await page.keyboard.press('Shift+Tab');
    await expect(page.getByRole('button')).toHaveCount(2);
    await expect(github).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(google).toBeFocused();
  });

  test('Enter on a provider keeps a visible focus target on the page while the redirect is pending', async ({
    page,
  }) => {
    // `stay`: the IdP answers without a new document, so the page the app left on screen is the page
    // asserted here — providers disabled, one start request, the heading holding the ring.
    const starts = await stubProviderIdp(page, 'stay');

    await page.goto(withVariant(localePath('en', DEEP_LINK), ANONYMOUS_SESSION));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');

    const google = providerButton(page, 'Google');
    const github = providerButton(page, 'GitHub');
    await page.keyboard.press('Tab');
    await expect(google).toBeFocused();
    await page.keyboard.press('Enter');

    // One start request for one activation, and the document never left.
    await expect.poll(() => starts.length).toBe(1);
    expect(starts[0]!.url.pathname).toBe('/v1/auth/oidc/google/start');
    expect(new URL(starts[0]!.callback ?? '/', starts[0]!.url).pathname).toBe('/en/auth/callback');
    expect(new URL(page.url()).pathname).toBe('/en/login');

    // The announcement is its own live region, outside the busy controls (never re-created, never
    // disabled with them), and both providers are disabled — the button that carried focus cannot
    // keep it.
    const status = page.getByRole('status');
    await expect(status).toHaveText('Opening your sign-in provider…');
    await expect(status).toHaveAttribute('aria-live', 'polite');
    await expect(page.locator('.login-providers')).toHaveAttribute('aria-busy', 'true');
    await expect(page.locator('[aria-busy="true"] [role="status"]')).toHaveCount(0);
    await expect(google).toBeDisabled();
    await expect(github).toBeDisabled();

    // F-LOGIN-02: focus does not sink to `body`. It moves to the page's own heading — the element a
    // Back return restores — and the ring is there to see while it waits.
    await expect(page.locator('#login-heading')).toBeFocused();
    expect(await activeElement(page)).toMatchObject({ tag: 'h1', id: 'login-heading' });
    await expect(page.locator('#login-heading')).toHaveCSS('outline-style', 'solid');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-width', '2px');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-color', 'rgb(0, 107, 102)');
  });

  test('a completed round trip lands in the workspace with focus in the route region', async ({
    page,
  }) => {
    const starts = await stubProviderIdp(page, 'land');

    await page.goto(withVariant(localePath('en', DEEP_LINK), ANONYMOUS_SESSION));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');

    await page.keyboard.press('Tab');
    await expect(providerButton(page, 'Google')).toBeFocused();
    await page.keyboard.press('Enter');
    await expect.poll(() => starts.length).toBe(1);

    // The round trip passes through the landing page, whose whole life is a transient status with no
    // interactive control: `CallbackPage.complete()` runs in `ngOnInit` and the fixture backend
    // answers the probe in-process, so the landing can redirect before its first meaningful paint and
    // is not observable from here. Its keyboard facts are pinned at template level by
    // `callback.page.spec.ts:179-191` (heading, "Checking your session…" status, no control), so the
    // observable end of the walkthrough is the handoff into the workspace.
    await page.waitForURL('**/auth/callback?**');

    // The callback's own redirect back into the workspace runs the shell handoff, so focus lands in
    // the route's region as soon as the shell is on screen.
    await expect.poll(() => new URL(page.url()).pathname).toBe(`/en${DEEP_LINK}`);
    await expect(page.locator('#main-content')).toBeFocused();
    await expect(page.locator('#main-content')).toHaveAttribute('tabindex', '-1');
  });

  test('a refused callback hands focus into /login instead of leaving it on the body', async ({
    page,
  }) => {
    // The landing answers its session probe with `401`, so it redirects CLIENT-side to
    // `/login?reason=failed` — the entry F-LOGIN-01 is about: no shell handoff runs on this page.
    await page.goto(
      withVariant(
        `${localePath('en', '/auth/callback')}?returnUrl=${encodeURIComponent('/')}`,
        ANONYMOUS_SESSION,
      ),
    );
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    expect(new URL(page.url()).searchParams.get('reason')).toBe('failed');
    await expect(page.getByRole('status')).toHaveText(
      'Sign-in failed. Choose a provider to try again.',
    );

    // F-LOGIN-01: the page owns the handoff, so the heading — the page's focus target — holds it.
    await expect(page.locator('#login-heading')).toBeFocused();
    expect(await activeElement(page)).toMatchObject({ tag: 'h1', id: 'login-heading' });
    await expect(page.locator('#login-heading')).toHaveAttribute('tabindex', '-1');

    // The indicator travels with the handoff, in a document where no key has been pressed: this
    // Chromium matches `:focus-visible` for programmatic focus, so the ring below is the visible
    // state a sighted visitor arrives at — not a UA default that a mouse-driven entry would lose.
    await expect(page.locator('#login-heading')).toHaveCSS('outline-style', 'solid');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-color', 'rgb(0, 107, 102)');

    // The order is unchanged: from the heading the next Tab is still the first provider.
    await page.keyboard.press('Tab');
    await expect(providerButton(page, 'Google')).toBeFocused();
  });

  test('an expiry redirect into /login keeps the document start and the provider order', async ({
    page,
  }) => {
    // The orgs read succeeds and the scope read answers `401` — both inside the guard of the
    // navigation the visitor asked for — so the store redirects to `/login?reason=expired` with the
    // attempted URL as `returnUrl`.
    await page.goto(`${localePath('en', DEEP_LINK)}${EXPIRED_SCOPE}`);
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    const login = new URL(page.url());
    expect(login.searchParams.get('reason')).toBe('expired');
    await expect(page.locator('.login-notice')).toContainText('Your session expired');
    await expect(page.getByRole('status')).toHaveAttribute('aria-live', 'polite');

    // This entry is the document's FIRST render: no navigation had ended in this document before the
    // guard redirect produced it, which is exactly the case a page load is (`Router.navigated` is
    // false — the same marker the shell uses). So focus keeps the browser's own start, and the
    // client-side handoff does not fire. Nothing is lost: the notice is a live region, and the first
    // Tab is still the first provider.
    expect(await activeElement(page)).toMatchObject({ tag: 'body' });
    await page.keyboard.press('Tab');
    await expect(providerButton(page, 'Google')).toBeFocused();
  });

  test('an expiry that interrupts a client-side navigation hands focus to the heading', async ({
    page,
  }) => {
    // F-LOGIN-01's client-side half, which the deep link above cannot reach (`Router.navigated` is
    // false there, so the document keeps the browser's own focus start). `/welcome` is the one shell
    // screen that resolves no scope, so the document boots authenticated with the expiry toggle
    // already seeded — `FixtureToggles` reads `location.search` once, in its constructor
    // (`core/dev-fixtures/fixture-toggles.ts:78-96`) — and no project list has been read yet.
    await page.goto(withVariant(localePath('en', '/welcome'), EXPIRED_SCOPE));
    await expect(page.locator('#main-content h1')).toHaveText('No organization yet');
    await expect(page.locator('app-shell')).toHaveCount(1);
    expect(new URL(page.url()).pathname).toBe('/en/welcome');

    // The document's own time origin: it proves the redirect below is a router navigation inside the
    // loaded app, not a second page load that happens to land on `/login`.
    const timeOrigin = await page.evaluate(() => performance.timeOrigin);

    // Skip link, then the route's own region — the shell's two fixed first stops.
    await page.keyboard.press('Tab');
    await expect(page.locator('a.skip-link')).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator('#main-content')).toBeFocused();

    // The page's own action is the next stop; Enter is the whole walk. That navigation resolves the
    // workspace scope, whose project-list read answers `401`, so the store performs the expiry
    // redirect itself (`session.store.ts:274-283`): `/login?reason=expired&returnUrl=…`.
    await page.keyboard.press('Tab');
    const workspace = page.getByRole('link', { name: 'Back to workspace' });
    await expect(workspace).toBeFocused();
    await page.keyboard.press('Enter');

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    const login = new URL(page.url());
    expect(login.searchParams.get('reason')).toBe('expired');
    expect(login.searchParams.get('returnUrl')).toBe('/');
    await expect(page.locator('.login-notice')).toContainText('Your session expired');
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(timeOrigin);

    // F-LOGIN-01: the page owns the handoff for this entry too, so the heading — its focus target —
    // holds the focus, and the ring below is the page's own focus style rather than a UA default.
    await expect(page.locator('#login-heading')).toBeFocused();
    expect(await activeElement(page)).toMatchObject({ tag: 'h1', id: 'login-heading' });
    await expect(page.locator('#login-heading')).toHaveAttribute('tabindex', '-1');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-style', 'solid');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-width', '2px');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-offset', '2px');
    await expect(page.locator('#login-heading')).toHaveCSS('outline-color', 'rgb(0, 107, 102)');

    // The order inside the page is unchanged: from the heading the next Tab is the first provider.
    await page.keyboard.press('Tab');
    await expect(providerButton(page, 'Google')).toBeFocused();
  });

  test('a signed-out tab explains itself and keeps the keyboard path to a provider', async ({
    page,
  }) => {
    await page.goto(
      `${withVariant(localePath('en', '/login'), ANONYMOUS_SESSION)}&reason=signed-out`,
    );
    await expect(page.locator('#login-heading')).toHaveText('Sign in to Evidgate');

    // Both paragraphs are the tab's own words: the HttpOnly cookie is still valid until server
    // sign-out lands, so nothing here may claim the session ended.
    const notice = page.locator('.login-notice');
    await expect(notice).toContainText('Signed out on this tab');
    await expect(notice).toContainText('Server sign-out is not available yet');
    await expect(notice).not.toContainText('session ended');
    await expect(page.getByRole('status')).toHaveAttribute('aria-live', 'polite');

    // The state is announced, not enforced: both providers stay operable and the keyboard path in is
    // unchanged — a page load keeps the browser's own start, and one Tab reaches the first provider.
    await expect(providerButton(page, 'Google')).toBeEnabled();
    await expect(providerButton(page, 'GitHub')).toBeEnabled();
    expect(await activeElement(page)).toMatchObject({ tag: 'body' });
    await page.keyboard.press('Tab');
    await expect(providerButton(page, 'Google')).toBeFocused();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
