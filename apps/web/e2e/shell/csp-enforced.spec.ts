// 09.1.1-T4 hand-over (S68 security review F1; STATUS "Shell e2e suite is not in CI"): the shell
// under the app-origin CSP and Trusted Types that the static server really sends.
//
// `playwright.fixtures.config.ts` sets `bypassCSP: true` for the whole shell suite, because the
// policy refuses the inline script `axeScan` injects. That leaves the shell itself unexercised
// under its own policy: a template binding, a lazy chunk or a DOM write that the policy blocks would
// pass every other spec here. This file turns enforcement back on (no axe in it) and asserts that
// booting the shell, rendering the project overview and navigating through the sidebar raise zero
// `securitypolicyviolation` events, in both locale bundles. `e2e/csp.spec.ts` covers the policy
// text on the production suite; this covers the shell's behaviour inside it.
import { expect, test, type Page } from '@playwright/test';

import { LOCALES, overviewPath } from './support';

test.use({ bypassCSP: false });

interface ViolationRecord {
  readonly directive: string;
  readonly blockedURI: string;
  readonly sample: string;
}

/** The global the init script installs, as `e2e/csp.spec.ts` declares its own probe. */
interface ProbeWindow {
  __violations?: ViolationRecord[];
}
/**
 * Records every violation from the first byte of the document. `securitypolicyviolation` does not
 * bubble, so the listener sits in the capture phase on `document`, as in `e2e/csp.spec.ts`.
 */
async function installViolationProbe(page: Page): Promise<void> {
  await page.addInitScript(() => {
    // `window` is the page's own global; the init script is what adds `__violations` to it.
    const probe = window as unknown as ProbeWindow;
    probe.__violations = [];
    document.addEventListener(
      'securitypolicyviolation',
      (event) => {
        probe.__violations?.push({
          directive: event.effectiveDirective,
          blockedURI: event.blockedURI,
          sample: event.sample,
        });
      },
      true,
    );
  });
}

async function violations(page: Page): Promise<ViolationRecord[]> {
  return page.evaluate(() => {
    const probe = window as unknown as ProbeWindow;
    return probe.__violations ?? [];
  });
}

for (const locale of LOCALES) {
  test(`the shell boots and navigates with the CSP enforced and no violation (${locale})`, async ({
    page,
  }) => {
    await installViolationProbe(page);

    const response = await page.goto(overviewPath(locale));
    // The policy is sent: the document answer carries it.
    expect(response?.headers()['content-security-policy'] ?? '').toContain(
      "require-trusted-types-for 'script'",
    );
    await expect(page.locator('#main-content h1')).toBeVisible();

    // One client-side navigation through the sidebar pulls another lazy chunk under the policy.
    const target = page.locator('app-shell-nav nav a:not([aria-current="page"])').first();
    const href = await target.getAttribute('href');
    expect(href).not.toBeNull();
    await target.click();
    await expect.poll(() => new URL(page.url()).pathname).toBe(href);
    await expect(page.locator('#main-content h1')).toBeVisible();

    expect(await violations(page)).toEqual([]);

    // Negative control: the policy is ENFORCED, not just sent. A raw string written into a
    // script sink must be refused by Trusted Types and reach the probe; with `bypassCSP` on, the
    // write would succeed silently and this step would fail.
    const refused = await page.evaluate(() => {
      try {
        document.createElement('script').textContent = 'void 0';
        return false;
      } catch {
        return true;
      }
    });
    expect(refused, 'Trusted Types refuses a raw script write').toBe(true);
    await expect
      .poll(async () => (await violations(page)).map((violation) => violation.directive))
      .toEqual(['require-trusted-types-for']);
  });
}
