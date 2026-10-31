import type { Page } from '@playwright/test';

/**
 * The `aria-describedby` ids that resolve to no rendered element.
 *
 * A description id is a promise the page makes to a screen reader, so a control described by an id
 * nothing carries is a broken reference rather than a missing nicety. An empty list is the pass;
 * one definition serves every spec, so the check cannot drift between suites.
 */
export async function danglingDescriptions(page: Page): Promise<readonly string[]> {
  return page.evaluate(() =>
    Array.from(document.querySelectorAll('[aria-describedby]')).flatMap((element) =>
      (element.getAttribute('aria-describedby') ?? '')
        .split(/\s+/)
        .filter(Boolean)
        .filter((id) => document.getElementById(id) === null),
    ),
  );
}
