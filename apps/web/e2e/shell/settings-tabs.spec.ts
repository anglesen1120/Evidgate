// 09.7.1-T1.3 (plan 09.7.1 §10 T1, §2 AC1; plan rows §8 S2/S3): the Settings tab strip in real
// Chromium, against the FIXTURES build. AC1 is "administer without seeing secrets": the strip names
// only the tabs a caller may open, and a tab it may not open is the SAME not-found page an address
// that never existed renders — so a direct URL confirms nothing about what exists.
//
// Every assertion keys on the strip's DOM contract (plan §10 T1.1) — `app-settings-shell`,
// `data-settings-tab`, `aria-current="page"`, `app-settings-tab-placeholder[data-settings-tab-body]`
// and `app-settings-no-tabs` — never on a localized label, so the same specs hold on both bundles.
// The session's grants come from corpus variants (`permissions-list-settings-*`), never from a role
// name: the client derives them from the `tenant_assignable` catalogue rows alone
// (`core/session/effective-permissions.source.ts`).
import { expect, test, type Page } from '@playwright/test';

import {
  activeSettingsTabId,
  expectNoA11yViolations,
  LOCALES,
  mainText,
  SETTINGS_ADMIN_GRANTS,
  SETTINGS_AUDIT_ONLY_GRANTS,
  seedStoredTheme,
  SETTINGS_NO_TAB_GRANTS,
  settingsPath,
  settingsTabBodyId,
  settingsTabIds,
  settingsTabPath,
  THEMES,
  withVariant,
} from './support';

/** The whole strip in strip order (`SETTINGS_TABS`), so an absent tab is named by its id. */
const TAB_IDS: readonly string[] = [
  'members',
  'secrets',
  'policy',
  'quota-retention',
  'audit',
  'security-events',
  'integrations',
  'agents',
];

/** The tabs whose CR-D-2 phase this build reached (G1): the only ones a grant set can reveal. */
const G1_TABS: readonly string[] = ['members', 'quota-retention', 'audit'];

/** What an address rendered, for the identity comparison a 404 claim needs. */
interface RenderedAddress {
  readonly pathname: string;
  readonly url: string;
  readonly heading: string;
  readonly text: string;
}

/**
 * Opens each address, records what the page says, and asserts each one rendered the not-found
 * pattern IN PLACE: same address still in the bar, one heading and one body text for all of them,
 * and never the forbidden page (a refusal leaking into the 404 copy would confirm the tab exists).
 */
async function expectIndistinguishableNotFound(
  page: Page,
  attempts: readonly string[],
): Promise<readonly RenderedAddress[]> {
  const rendered: RenderedAddress[] = [];
  for (const attempted of attempts) {
    const address = new URL(attempted, 'http://127.0.0.1');
    await page.goto(attempted);
    await expect(page.locator('#main-content h1')).toHaveCount(1);
    await expect(page.locator('app-not-found-pattern')).toHaveCount(1);
    await expect(page.locator('app-forbidden-pattern')).toHaveCount(0);
    rendered.push({
      pathname: address.pathname,
      url: page.url(),
      heading: (await page.locator('#main-content h1').innerText()).trim(),
      text: await mainText(page),
    });
  }

  for (const [index, entry] of rendered.entries()) {
    const landed = new URL(entry.url).pathname;
    expect(landed, `attempt ${index} kept the address it was given`).toBe(entry.pathname);
    expect(landed.endsWith('/forbidden'), `attempt ${index} is not the refusal page`).toBe(false);
  }

  expect(new Set(rendered.map((entry) => entry.heading)).size).toBe(1);
  expect(new Set(rendered.map((entry) => entry.text)).size).toBe(1);
  return rendered;
}

/**
 * The `data-settings-tab` ids focus visits, in the order the keyboard hands them out. Stops on
 * anything else are ignored, so the strip's order is what is measured, not the shell's whole focus
 * order (the sidebar contributes rows whose count depends on the grant set).
 */
async function keyboardTabOrder(page: Page, expected: readonly string[]): Promise<string[]> {
  const seen: string[] = [];
  // Start at the main landmark, as the skip link does: tabbing through the topbar first would blur
  // the project switcher, which commits a scope change on blur (a shell behaviour recorded as a
  // STATUS §6 row, not this strip's concern).
  await page.locator('#main-content').focus();
  // The shell's chrome is bounded (skip link, switchers, sidebar, main), so a fixed budget is
  // enough; the loop stops as soon as every expected tab has been reached.
  for (let press = 0; press < 80 && seen.length < expected.length; press += 1) {
    await page.keyboard.press('Tab');
    const id = await page.evaluate(
      () => document.activeElement?.getAttribute('data-settings-tab') ?? null,
    );
    if (id !== null && !seen.includes(id)) {
      seen.push(id);
    }
  }
  return seen;
}

test.describe('the tab strip names only tabs the caller may open (AC1)', () => {
  for (const locale of LOCALES) {
    test(`a session holding every tab code sees exactly the G1 tabs (${locale})`, async ({
      page,
    }) => {
      await page.goto(withVariant(settingsPath(locale), SETTINGS_ADMIN_GRANTS));

      // The index address redirects to the first tab the session may open, so the strip has an
      // active tab on arrival — never an empty selection.
      await expect
        .poll(() => new URL(page.url()).pathname)
        .toBe(settingsTabPath(locale, 'members'));
      await expect(page.locator('html')).toHaveAttribute('lang', locale);
      await expect(page.locator('app-settings-shell')).toHaveCount(1);

      await expect.poll(() => settingsTabIds(page)).toEqual(G1_TABS);
      // Each tab is a real link (an `a` with an `href`), not a button-shaped span: the strip's
      // keyboard and middle-click behaviour, and the `aria-current` state, depend on that.
      expect(
        await page
          .locator('app-settings-shell [data-settings-tab]')
          .evaluateAll((tabs) =>
            tabs.map((tab) => `${tab.tagName.toLowerCase()}:${tab.getAttribute('href') !== null}`),
          ),
      ).toEqual(G1_TABS.map(() => 'a:true'));
      // The five later-phase tabs are absent, not disabled or teased (CR-D-2: no existence leak).
      for (const hidden of TAB_IDS.filter((id) => !G1_TABS.includes(id))) {
        await expect(
          page.locator(`[data-settings-tab="${hidden}"]`),
          `the strip holds no '${hidden}' tab in this build`,
        ).toHaveCount(0);
      }

      const strip = page.locator('app-settings-shell nav:has([data-settings-tab])');
      await expect(strip).toHaveCount(1);
      await expect(strip).toHaveAttribute('aria-label', /\S/);
      await expect.poll(() => activeSettingsTabId(page)).toBe('members');
      await expect.poll(() => settingsTabBodyId(page)).toBe('members');
    });
  }

  test('a session holding one tab is redirected to it from the index address', async ({ page }) => {
    await page.goto(withVariant(settingsPath('en'), SETTINGS_AUDIT_ONLY_GRANTS));

    await expect.poll(() => new URL(page.url()).pathname).toBe(settingsTabPath('en', 'audit'));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);
    await expect(page.locator('app-settings-no-tabs')).toHaveCount(0);

    await expect.poll(() => settingsTabIds(page)).toEqual(['audit']);
    await expect.poll(() => activeSettingsTabId(page)).toBe('audit');
    await expect.poll(() => settingsTabBodyId(page)).toBe('audit');
  });

  test('a session with no reachable tab gets the empty state, not a refusal and not a redirect', async ({
    page,
  }) => {
    const index = withVariant(settingsPath('en'), SETTINGS_NO_TAB_GRANTS);
    await page.goto(index);

    await expect(page.locator('app-settings-no-tabs')).toHaveCount(1);
    await expect(page.locator('[data-settings-tab]')).toHaveCount(0);
    await expect(page.locator('app-forbidden-pattern')).toHaveCount(0);
    // Still inside the shell, on the address the visitor typed: the entry's own grant
    // (`organization:read`) opened the route, and the strip is simply empty.
    await expect(page.locator('app-shell')).toHaveCount(1);
    expect(new URL(page.url()).pathname).toBe(settingsPath('en'));
  });
});

test.describe('a tab the caller may not open is the not-found page (AC1, §8 S2/S3)', () => {
  test('a tab outside the grant set renders the same page an unknown path renders', async ({
    page,
  }) => {
    const rendered = await expectIndistinguishableNotFound(page, [
      withVariant(settingsTabPath('en', 'members'), SETTINGS_AUDIT_ONLY_GRANTS),
      withVariant(settingsTabPath('en', 'zzz-unknown'), SETTINGS_AUDIT_ONLY_GRANTS),
    ]);

    // The page never echoes the tab it refused: the attempted segment is absent from the copy.
    expect(rendered.map((entry) => entry.text).join(' ')).not.toContain('members');
  });

  test('a tab whose phase this build has not reached is the same not-found page (Agents, G4)', async ({
    page,
  }) => {
    // The grant set holds `agent_pool:read`, so the 404 cannot be a permission refusal: the tab's
    // phase (G4) is what keeps it out, and the answer is the same indistinguishable one.
    await page.goto(withVariant(settingsTabPath('en', 'members'), SETTINGS_ADMIN_GRANTS));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);
    await expect(page.locator('[data-settings-tab="agents"]')).toHaveCount(0);

    const rendered = await expectIndistinguishableNotFound(page, [
      withVariant(settingsTabPath('en', 'agents'), SETTINGS_ADMIN_GRANTS),
      withVariant(settingsTabPath('en', 'zzz-unknown'), SETTINGS_ADMIN_GRANTS),
    ]);

    expect(rendered.map((entry) => entry.text).join(' ')).not.toContain('agents');
  });
});

test.describe('the strip is operable from the keyboard (negative control)', () => {
  // A strip that swallowed focus (links at `tabindex="-1"`) or reordered its tabs would still pass
  // every assertion above: the ids come from the DOM order, not from the focus order. Tab must
  // therefore reach each visible tab link, in strip order, before this slice's claims hold.
  test('Tab reaches each visible tab link, in strip order', async ({ page }) => {
    await page.goto(withVariant(settingsTabPath('en', 'members'), SETTINGS_ADMIN_GRANTS));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);

    expect(await keyboardTabOrder(page, G1_TABS)).toEqual(G1_TABS);
  });
});

test.describe('the Settings shell is WCAG 2.2 AA clean (AC1, 00 §5.6 W9)', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`tab strip and placeholder body, ${locale} bundle, ${theme} tokens`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.goto(withVariant(settingsTabPath(locale, 'members'), SETTINGS_ADMIN_GRANTS));
        await expect(page.locator('app-settings-shell')).toHaveCount(1);
        await expect.poll(() => settingsTabIds(page)).toEqual(G1_TABS);

        await expectNoA11yViolations(page, `settings ${locale}/${theme}`);
      });
    }
  }

  test('the no-tabs state is clean', async ({ page }) => {
    await page.goto(withVariant(settingsPath('en'), SETTINGS_NO_TAB_GRANTS));
    await expect(page.locator('app-settings-no-tabs')).toHaveCount(1);

    await expectNoA11yViolations(page, 'settings no-tabs en/light');
  });

  test('at 360px the strip is a select and the page does not scroll sideways', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 740 });
    await page.goto(withVariant(settingsTabPath('en', 'members'), SETTINGS_ADMIN_GRANTS));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
    await expectNoA11yViolations(page, 'settings 360px en/light');
  });
});
