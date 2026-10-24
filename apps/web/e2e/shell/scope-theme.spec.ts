// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC4/AC5; 00 §5.6 W9): the two pieces of shell state that
// outlive a page — the scope switcher (which rewrites the URL, never the session) and the theme
// choice (which persists, and whose ABSENCE must stay visible to CSS). Both are properties of the
// real browser: `localStorage`, `matchMedia` and history all behave differently in jsdom.
import { expect, test } from '@playwright/test';

import {
  currentTheme,
  LIMITED_GRANTS,
  localePath,
  ORG_ACME,
  ORG_LUMEN,
  overviewPath,
  PROJECT_CHECKOUT,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  withVariant,
} from './support';

/** `/orgs/:orgId/projects/:projectId/authoring` — the default corpus session reaches it. */
const AUTHORING_PATH = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/authoring`;
const AUTHORING = localePath('en', AUTHORING_PATH);

test.describe('scope switcher (AC4)', () => {
  test('choosing a project keeps the rest of the path', async ({ page }) => {
    // `project:read` is not in the default corpus session, so the project screen is reached through
    // the trimmed variant, which grants it.
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    const projectSelect = page.locator('app-scope-switcher select').nth(1);
    await expect(projectSelect).toBeEnabled();
    await expect(projectSelect).toHaveValue(PROJECT_STOREFRONT);

    await projectSelect.selectOption(PROJECT_CHECKOUT);
    await expect
      .poll(() => new URL(page.url()).pathname)
      .toBe(`/en/orgs/${ORG_ACME}/projects/${PROJECT_CHECKOUT}/overview`);
    // The segment after the project id is preserved, not reset to the entry route: the visitor
    // keeps reading the same screen of another project.
    await expect(page.locator('#main-content h1')).toHaveText('Checkout API');
    // And the fixture variant that selected this session is still on the URL, because the switcher
    // rewrites segments of the current URL rather than building a new one.
    expect(new URL(page.url()).searchParams.get('fixture.variant.listPermissions')).toBe('limited');
  });

  test('choosing a project from an org-scope route opens its project screen', async ({ page }) => {
    await page.goto(withVariant(localePath('en', `/orgs/${ORG_ACME}/audit`), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toBeVisible();

    const projectSelect = page.locator('app-scope-switcher select').nth(1);
    await expect(projectSelect).toBeEnabled();
    await projectSelect.selectOption(PROJECT_CHECKOUT);

    // No project segment existed to rewrite, so the switcher navigates to the entry screen.
    await expect
      .poll(() => new URL(page.url()).pathname)
      .toBe(`/en/orgs/${ORG_ACME}/projects/${PROJECT_CHECKOUT}/overview`);
  });

  test('choosing an organization moves the URL into that organization', async ({ page }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));

    const orgSelect = page.locator('app-scope-switcher select').first();
    await expect(orgSelect).toBeEnabled();
    await expect(orgSelect).toHaveValue(ORG_ACME);
    // The switcher lists exactly the organizations the session read, no more.
    expect(await orgSelect.locator('option[value]:not([value=""])').count()).toBe(2);

    await orgSelect.selectOption(ORG_LUMEN);

    // What the switcher owns is the attempt: the address now names the organization that was
    // chosen. (The corpus lists org 1's projects only, so org 2's landing resolves an organization
    // with a project list whose ids carry another `org_id`; the project check refuses that pair and
    // the shell renders its not-found pattern — a fixture shape, not a shell decision.)
    // The address settles on the landing redirect's target, not on bare `/orgs/<org>`:
    // `orgLandingGuard` resolves the organization's first project in the same navigation.
    await expect
      .poll(() => new URL(page.url()).pathname)
      .toBe(`/en/orgs/${ORG_LUMEN}/projects/${PROJECT_STOREFRONT}/overview`);
    await expect(page.locator('#main-content h1')).toHaveText('Page not found');
  });
});

test.describe('theme preference (AC5)', () => {
  test('no stored choice leaves data-theme unset, even when the system prefers dark', async ({
    page,
  }) => {
    // The `page` fixture starts a fresh context: storage is empty, which IS "no choice was made".
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();

    // The absence IS the contract: the tokens follow `prefers-color-scheme`, so a stylesheet can
    // tell "chosen light" from "light by system" and an explicit choice can always win.
    expect(await currentTheme(page)).toBeNull();
    // The control still reports the effective theme, so it is not lying about what is on screen.
    await expect(page.locator('button.theme-toggle')).toHaveAttribute('aria-pressed', 'true');

    // With the same absence and a light system the control reports light.
    await page.emulateMedia({ colorScheme: 'light' });
    await expect(page.locator('button.theme-toggle')).toHaveAttribute('aria-pressed', 'false');
    expect(await currentTheme(page)).toBeNull();
  });

  test('the toggle stores an explicit choice that survives a reload', async ({ page }) => {
    // No `seedStoredTheme` here: an `addInitScript` also runs on `reload()` and would clear the
    // very key this case has to survive. The fresh context already starts with empty storage.
    await page.emulateMedia({ colorScheme: 'light' });
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();
    expect(await currentTheme(page)).toBeNull();

    const toggle = page.locator('button.theme-toggle');
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await toggle.click();

    expect(await currentTheme(page)).toBe('dark');
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    expect(await page.evaluate(() => localStorage.getItem('evidgate-theme'))).toBe('dark');

    await page.reload();
    await expect(page.locator('#main-content h1')).toBeVisible();
    expect(await currentTheme(page)).toBe('dark');
    await expect(page.locator('button.theme-toggle')).toHaveAttribute('aria-pressed', 'true');

    // A second activation pins the other choice rather than returning to "no choice".
    await page.locator('button.theme-toggle').click();
    expect(await currentTheme(page)).toBe('light');
    expect(await page.evaluate(() => localStorage.getItem('evidgate-theme'))).toBe('light');
  });

  test('a stored choice overrides the opposite system preference', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await seedStoredTheme(page, 'light');
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();

    expect(await currentTheme(page)).toBe('light');
    await expect(page.locator('button.theme-toggle')).toHaveAttribute('aria-pressed', 'false');
  });

  test('the choice is per browser, not per session: a fresh context starts with no choice', async ({
    browser,
  }) => {
    const first = await browser.newContext({ colorScheme: 'light' });
    const firstPage = await first.newPage();
    await firstPage.goto(AUTHORING);
    await expect(firstPage.locator('#main-content h1')).toBeVisible();
    await firstPage.locator('button.theme-toggle').click();
    expect(await currentTheme(firstPage)).toBe('dark');
    await first.close();

    const second = await browser.newContext({ colorScheme: 'light' });
    const secondPage = await second.newPage();
    await secondPage.goto(AUTHORING);
    await expect(secondPage.locator('#main-content h1')).toBeVisible();
    expect(await currentTheme(secondPage)).toBeNull();
    await second.close();
  });
});
