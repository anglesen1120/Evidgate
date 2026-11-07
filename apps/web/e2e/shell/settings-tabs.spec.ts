// 09.7.1-T1.3 (plan 09.7.1 §10 T1, §2 AC1; plan rows §8 S2/S3): the Settings tab strip in real
// Chromium, against the FIXTURES build. AC1 is "administer without seeing secrets": the strip names
// only the tabs a caller may open, and a tab it may not open is the SAME not-found page an address
// that never existed renders — so a direct URL confirms nothing about what exists.
//
// Every assertion keys on the strip's DOM contract (plan §10 T1.1) — `app-settings-shell`,
// `data-settings-tab`, `aria-current="page"`, `app-settings-tab-placeholder[data-settings-tab-body]`
// for a tab whose story has not shipped a body yet, `app-members-page` for the one that has
// (09.7.1-T2), and `app-settings-no-tabs` — never on a localized label, so the same specs hold on
// both bundles.
// The session's grants come from the permission catalogue, never from a role name: the client
// derives them from the `tenant_assignable` rows alone
// (`core/session/effective-permissions.source.ts`). The sets are corpus variants
// (`permissions-list-settings-*`) except `settings-admin-usage`, the strip's admin session, which
// is the admin corpus page plus the corpus's `usage:read` row as a computed specimen
// (`core/dev-fixtures/fixture-settings-permissions.ts`) — the Quota & retention tab's own read.
import { expect, test, type Page } from '@playwright/test';

import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  activeSettingsTabId,
  expectIndistinguishableNotFound,
  LOCALES,
  SETTINGS_ADMIN_GRANTS,
  SETTINGS_ADMIN_USAGE_GRANTS,
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
      await page.goto(withVariant(settingsPath(locale), SETTINGS_ADMIN_USAGE_GRANTS));

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
      // 09.7.1-T2 shipped the members tab body: the host is the real page, not the placeholder, so
      // `settingsTabBodyId` (which answers placeholder bodies) must return null here.
      await expect(page.locator('app-members-page')).toHaveCount(1);
      expect(await settingsTabBodyId(page)).toBeNull();
    });
  }

  test('a session holding one tab is redirected to it from the index address', async ({ page }) => {
    await page.goto(withVariant(settingsPath('en'), SETTINGS_AUDIT_ONLY_GRANTS));

    await expect.poll(() => new URL(page.url()).pathname).toBe(settingsTabPath('en', 'audit'));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);
    await expect(page.locator('app-settings-no-tabs')).toHaveCount(0);

    await expect.poll(() => settingsTabIds(page)).toEqual(['audit']);
    await expect.poll(() => activeSettingsTabId(page)).toBe('audit');
    // 09.7.1-T6 shipped the audit tab body: the host is the real page, not the placeholder, so
    // `settingsTabBodyId` (placeholder bodies only) answers null, as it does for members.
    await expect(page.locator('app-audit-page')).toHaveCount(1);
    expect(await settingsTabBodyId(page)).toBeNull();
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
    await page.goto(withVariant(settingsTabPath('en', 'members'), SETTINGS_ADMIN_USAGE_GRANTS));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);

    expect(await keyboardTabOrder(page, G1_TABS)).toEqual(G1_TABS);
  });
});

test.describe('the Settings shell is WCAG 2.2 AA clean (AC1, 00 §5.6 W9)', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`tab strip and members body, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(
          withVariant(settingsTabPath(locale, 'members'), SETTINGS_ADMIN_USAGE_GRANTS),
        );
        await expect(page.locator('app-settings-shell')).toHaveCount(1);
        await expect.poll(() => settingsTabIds(page)).toEqual(G1_TABS);

        // The members body is a live page since 09.7.1-T2, not a static placeholder: scan it once
        // its first page of memberships has landed, so the audit sees the table and the role matrix
        // instead of the loading skeleton.
        const members = page.locator('app-members-page');
        await expect(members).toHaveAttribute('data-members-phase', 'ready');

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
    await page.goto(withVariant(settingsTabPath('en', 'members'), SETTINGS_ADMIN_USAGE_GRANTS));
    await expect(page.locator('app-settings-shell')).toHaveCount(1);
    // The widest body of the strip: the real members table, so the overflow measurement is of the
    // table in its ready state rather than of a placeholder (09.7.1-T2).
    const members = page.locator('app-members-page');
    await expect(members).toHaveAttribute('data-members-phase', 'ready');
    const destination = page.locator('app-settings-shell select#settings-destination');
    await expect(destination).toBeVisible();
    await expect(destination).toHaveValue('members');
    await expect(destination.locator('option')).toHaveCount(G1_TABS.length);
    for (const link of await page.locator('app-settings-shell [data-settings-tab]').all()) {
      await expect(link).toBeHidden();
    }

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
    await expectNoA11yViolations(page, 'settings 360px en/light');
  });
});
