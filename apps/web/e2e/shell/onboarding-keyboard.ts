// The keyboard-only walkthrough helpers the onboarding a11y specs share. Every interaction goes
// through `page.keyboard` — no `page.click`, no `locator.click`, no `locator.focus()`, no
// `{ force: true }` — and every stop is asserted by element identity (test id, id, role) plus the
// focus ring the control renders. A programmatic focus move would prove nothing about the tab order
// a visitor really crosses, so none of these helpers performs one.
import { expect, type Locator, type Page } from '@playwright/test';

import { stepItem, type StepNumber } from './onboarding-support';

/** The shell's skip link: the first stop of a fresh document. */
export const SKIP_LINK = 'a.skip-link';
/** The shell's main region, the skip target and the anchor the walkthrough resumes from. */
export const MAIN = '#main-content';
/** The wizard's own step heading — the announcement target of every step move. */
export const STEP_TITLE = '#onboarding-step-title';
/**
 * The quickstart's focusable block: the `<pre tabindex="0">` is the Tab stop, while its
 * `data-testid="cli-quickstart-snippet"` sits on the `<code>` it renders.
 */
export const QUICKSTART_SNIPPET = '[data-testid="cli-quickstart"] pre';

/** One stepper item's button, addressed by the item's DOM contract rather than by its label. */
export function stepButton(page: Page, step: StepNumber): Locator {
  return stepItem(page, step).locator('button');
}

/** Presses Tab `times` times: the only way these specs move focus forward. */
export async function pressTab(page: Page, times = 1): Promise<void> {
  for (let index = 0; index < times; index += 1) {
    await page.keyboard.press('Tab');
  }
}

/**
 * Puts focus on the wizard's main region with real keys only, for the walks that start inside it:
 * a fresh document starts its tab order at the shell's skip link, and Enter on it activates the
 * skip target (`main#main-content`, `tabindex="-1"`). Both stops are asserted by identity and by
 * their focus ring, because they are two stops a keyboard visitor really crosses on the way in.
 */
export async function enterMainByKeyboard(page: Page): Promise<void> {
  await pressTab(page);
  await expectKeyboardFocus(page, page.locator(SKIP_LINK), 'the skip link before the walk');
  await page.keyboard.press('Enter');
  await expectKeyboardFocus(page, page.locator(MAIN), 'the main region before the walk');
}

/**
 * The focused control is visibly focused, not merely focused. Every focusable in these flows
 * declares the `--eg-focus` ring (2px, 2px offset); an `outline: none` anywhere on that path would
 * leave a keyboard visitor guessing, so the computed outline is what is asserted.
 *
 * Every call follows a Tab or Enter — these specs move focus only with real key presses — which is
 * what makes the assertion honest for the two targets styled `:focus-visible` only
 * (`main#main-content`, `#onboarding-step-title`): both are reached by an Enter, so the browser
 * reports them as keyboard-focused. The headings the pages focus themselves after a step move (the
 * reveal heading, the connect panel headings, `#enable-enabled-heading`) are styled on `:focus`
 * instead, so they render a ring either way.
 */
export async function expectFocusRing(page: Page, context: string): Promise<void> {
  const focused = await page.evaluate(() => {
    const element = document.activeElement;
    if (element === null) return null;
    const style = getComputedStyle(element);
    return {
      tag: element.tagName.toLowerCase(),
      id: element.id,
      testId: element.getAttribute('data-testid'),
      outlineStyle: style.outlineStyle,
      outlineWidth: style.outlineWidth,
    };
  });

  const name =
    focused === null
      ? 'nothing'
      : `${focused.tag}${focused.id === '' ? '' : `#${focused.id}`}${
          focused.testId === null ? '' : `[data-testid="${focused.testId}"]`
        }`;

  expect(focused, `${context}: nothing holds focus`).not.toBeNull();
  expect(focused?.outlineStyle, `${context}: ${name} renders no focus outline`).not.toBe('none');
  expect(
    Number.parseFloat(focused?.outlineWidth ?? '0'),
    `${context}: ${name} renders a zero-width focus outline`,
  ).toBeGreaterThan(0);
}

/** Asserts the focused element by identity, then that it is visibly focused. */
export async function expectKeyboardFocus(
  page: Page,
  locator: Locator,
  context: string,
): Promise<void> {
  await expect(locator, `${context}: focus identity`).toBeFocused();
  await expectFocusRing(page, context);
}

/**
 * Tabs forward until `selector` holds focus, returning the stops crossed. A walk into a form whose
 * Tab order belongs to other slices uses this instead of a programmatic focus move: every stop on
 * the way is one a keyboard visitor really crosses, and a target the walk cannot reach fails with
 * the list of stops it did reach.
 */
export async function tabUntil(
  page: Page,
  selector: string,
  limit = 40,
): Promise<readonly string[]> {
  const crossed: string[] = [];
  for (let index = 0; index < limit; index += 1) {
    await page.keyboard.press('Tab');
    const reached = await page.evaluate((target) => {
      const element = document.activeElement as HTMLElement | null;
      if (element === null) return { hit: false, stop: 'nothing' };
      const name =
        element.id !== ''
          ? `#${element.id}`
          : `${element.tagName.toLowerCase()}${
              element.getAttribute('data-testid') === null
                ? ''
                : `[data-testid="${element.getAttribute('data-testid')}"]`
            }`;
      return { hit: element.matches(target), stop: name };
    }, selector);
    if (reached.hit) return crossed;
    crossed.push(reached.stop);
  }
  throw new Error(`${selector} is not in the Tab order; stopped at ${crossed.join(' → ')}`);
}

/**
 * Every enabled Tab stop inside the main region, named by its DOM contract. The identity list is
 * the budget assertion: a stray stop (a new control, a `tabindex="0"` wrapper) shows up by name, so
 * the check cannot pass by counting alone.
 */
export async function enabledTabStops(page: Page): Promise<readonly string[]> {
  return page.evaluate(() => {
    const main = document.querySelector('#main-content');
    if (main === null) return [];

    return Array.from(
      main.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex]'),
    )
      .filter((element) => {
        if (element.tabIndex < 0) return false;
        if (element.matches(':disabled')) return false;
        if (element.closest('[inert]') !== null) return false;
        const style = getComputedStyle(element);
        if (style.visibility === 'hidden' || style.display === 'none') return false;
        return element.getClientRects().length > 0;
      })
      .map((element) => {
        const step = element.closest('[data-testid^="onboarding-step-"]');
        if (step !== null) return `${step.getAttribute('data-testid')} button`;
        const testId = element.getAttribute('data-testid');
        if (testId !== null) return testId;
        if (element.id !== '') return `#${element.id}`;
        return element.tagName.toLowerCase();
      });
  });
}
