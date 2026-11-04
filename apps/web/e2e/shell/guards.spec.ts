// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC4/AC5; 00 §9.2): the two refusals that render INSIDE the
// shell — a permission the session lacks (`/forbidden`) and an address that resolves to nothing
// (the single wildcard). Both are asserted in real Chromium, because both depend on the router's
// match ordering, which a jsdom spec can only mirror.
//
// The not-found case is the security-relevant one: a foreign organization, a foreign project, an
// unknown path and a phase-hidden entry must be indistinguishable. One spec compares all four.
import { expect, test } from '@playwright/test';
import { scopeControl, scopeOption } from './scope-controls';

import {
  LIMITED_GRANTS,
  localePath,
  mainText,
  ORG_ACME,
  ORG_LUMEN,
  PROJECT_STOREFRONT,
  UNKNOWN_ORG,
  UNKNOWN_PROJECT,
  withVariant,
} from './support';

test.describe('a missing permission (AC4)', () => {
  // The default corpus session holds neither `organization:read` nor `usage:read`, so both routes
  // exist and are refused: this is the 403 path, not the 404 path.
  const refused = [
    {
      name: 'an org-scope entry (`settings` needs `organization:read`)',
      path: `/orgs/${ORG_ACME}/settings`,
    },
    {
      name: 'a second org-scope entry (`usage` needs `usage:read`)',
      path: `/orgs/${ORG_ACME}/usage`,
    },
  ];

  for (const { name, path } of refused) {
    test(`${name} redirects to /forbidden without echoing the attempt`, async ({ page }) => {
      const attempted = localePath('en', path);
      await page.goto(attempted);

      // The guard answers with a UrlTree, so the address becomes the pattern address and stays
      // there: no `returnUrl`, no fragment of the attempt.
      await expect.poll(() => new URL(page.url()).pathname).toBe('/en/forbidden');
      expect(new URL(page.url()).search).toBe('');

      await expect(page.locator('#main-content h1')).toHaveText('Not authorized');
      await expect(page.locator('app-forbidden-pattern h2')).toHaveText('Not authorized');
      await expect(page.locator('#main-content')).not.toContainText(path);
      // Still inside the shell: a refusal is a page, not an ejection.
      await expect(page.locator('app-shell')).toHaveCount(1);
      await expect(page.locator('app-forbidden-pattern')).toHaveCount(1);
    });
  }

  test('the refusal is the same page whatever the locale of the bundle', async ({ page }) => {
    await page.goto(localePath('vi', `/orgs/${ORG_ACME}/settings`));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/vi/forbidden');
    await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
    await expect(page.locator('#main-content h1')).toHaveText('Không có quyền truy cập');
    await expect(page.locator('app-forbidden-pattern h2')).toHaveText('Không có quyền truy cập');
  });
});

test.describe('an address that resolves to nothing (AC4, 00 §9.2)', () => {
  test('a foreign org, a foreign project, an unknown path and a phase-hidden entry are indistinguishable', async ({
    page,
  }) => {
    const attempts = [
      // A well-formed id no list in the corpus holds.
      localePath('en', `/orgs/${UNKNOWN_ORG}`),
      // A valid organization with a project that belongs to no organization.
      localePath('en', `/orgs/${ORG_ACME}/projects/${UNKNOWN_PROJECT}/overview`),
      // A path that matches no entry at all.
      localePath('en', `/orgs/${ORG_ACME}/not-a-page`),
      // A real entry of a phase this build has not reached (G2 `runs`): hidden, never teased.
      withVariant(
        localePath('en', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs`),
        LIMITED_GRANTS,
      ),
    ];

    const rendered: {
      pathname: string;
      url: string;
      title: string;
      heading: string;
      text: string;
    }[] = [];
    for (const attempted of attempts) {
      const address = new URL(attempted, 'http://127.0.0.1');
      await page.goto(attempted);
      await expect(page.locator('#main-content h1')).toHaveText('Not found');
      await expect(page.locator('app-not-found-pattern h2')).toHaveText('Not found');
      rendered.push({
        pathname: address.pathname,
        url: page.url(),
        title: await page.title(),
        heading: (await page.locator('#main-content h1').innerText()).trim(),
        text: await mainText(page),
      });
    }

    // 1. The wildcard rendered in place: no redirect, no fallback to the landing route, so the
    //    address a visitor typed is still the address the browser shows.
    for (const [index, entry] of rendered.entries()) {
      expect(new URL(entry.url).pathname, `attempt ${index} kept its path`).toBe(entry.pathname);
    }

    // 2. One page heading, shared pattern and document title: the four addresses cannot be
    //    told apart from what the page says. This is the property that keeps a foreign organization
    //    id from confirming that the id exists somewhere else.
    expect(new Set(rendered.map((entry) => entry.heading)).size).toBe(1);
    expect(new Set(rendered.map((entry) => entry.text)).size).toBe(1);
    expect(new Set(rendered.map((entry) => entry.title)).size).toBe(1);
    expect(rendered[0]?.text).toContain(
      'This page does not exist or you do not have access to it.',
    );
    expect(rendered[0]?.title).toBe('Not found · Evidgate');

    // 3. And the not-found page is not the forbidden page: a refusal that leaked into the 404 copy
    //    would tell a visitor that the resource exists.
    expect(rendered[0]?.text).not.toContain('Not authorized');
    // The shell is around it, with the navigation the session is allowed to use.
    await expect(page.locator('app-shell')).toHaveCount(1);
    // The corpus lists two organizations; a foreign id never renames the session's own scope.
    const orgSwitcher = scopeControl(page, 0);
    await expect(orgSwitcher).toBeEnabled();
    await orgSwitcher.click();
    await expect(page.getByRole('option')).toHaveCount(2);
    await expect(scopeOption(page, ORG_LUMEN)).toHaveCount(1);
    await page.keyboard.press('Escape');
  });

  test('the same unknown path inside a locale bundle renders that locale’s not-found copy', async ({
    page,
  }) => {
    await page.goto(localePath('vi', `/orgs/${ORG_ACME}/not-a-page`));
    await expect(page.locator('#main-content h1')).toHaveText('Không tìm thấy');
    await expect(page.locator('app-not-found-pattern h2')).toHaveText('Không tìm thấy');
    await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
  });
});
