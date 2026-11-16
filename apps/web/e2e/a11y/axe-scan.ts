// The real-browser half of the a11y gate: it injects axe into a live Playwright page, so it cannot
// live in `shell/support.ts` beside the fixture and routing helpers, and it must run the same
// options and the same exception list as the jsdom specs in `axe-config.ts`.
import { expect, type Page } from '@playwright/test';
import axe from 'axe-core';
import type { AxeResults, RunOptions } from 'axe-core';

import exceptionsDocument from '../a11y-exceptions.json';
import {
  AXE_RUN_OPTIONS,
  formatViolations,
  unexceptedViolations,
  type A11yException,
} from './axe-config';

/** The accepted violations every scan here honours; the checker in `scripts/` validates the file. */
const A11Y_EXCEPTIONS: readonly A11yException[] = exceptionsDocument.exceptions;

/** The subset of the injected `axe` global these specs use. */
interface AxeGlobal {
  run: (context: Document, options: RunOptions) => Promise<AxeResults>;
}

/**
 * Brain's trigger gets `role="combobox"` from a static host attribute, but `aria-expanded` and its
 * projected label arrive with the bindings and content, and the shell renders the scope switcher
 * through `@defer (on idle)`. A scan can catch a combobox one frame old — unnamed, unexpanded —
 * which axe reports as `aria-required-attr` plus `button-name`: a render race, not a page defect.
 * Wait for the finished DOM; after the bound wait the scan still runs, so a combobox that never
 * settles fails on axe's own message and selector.
 */
async function waitForSettledComboboxes(page: Page): Promise<void> {
  try {
    await page.waitForFunction(
      () => {
        const settled = (combobox: Element): boolean => {
          if (combobox.getAttribute('aria-expanded') === null) return false;
          const labelled =
            (combobox.getAttribute('aria-label') ?? '').trim() !== '' ||
            (combobox.getAttribute('aria-labelledby') ?? '').trim() !== '';
          return labelled || (combobox.textContent ?? '').trim() !== '';
        };
        return Array.from(document.querySelectorAll('[role="combobox"]')).every(settled);
      },
      undefined,
      { timeout: 5_000 },
    );
  } catch {
    // Not a wait to fail on: axe names the offending element on its own, and that is the report.
  }
}

/**
 * Runs axe in the real page, in real Chromium, with `color-contrast` ENABLED — the difference to
 * the jsdom gallery spec, which has no layout and must disable that rule.
 *
 * axe is injected from the workspace's own `axe-core` devDependency (`axe.source`), so no package
 * is added and the rule set is the version the repo pins.
 */
export async function axeScan(page: Page, context?: string): Promise<AxeResults> {
  await waitForSettledComboboxes(page);
  // A prior click can leave unrelated controls partly under sticky chrome. Normalize the
  // document viewport before measuring target geometry; do not exclude those controls.
  await page.evaluate(async () => {
    window.scrollTo({ top: 0, left: 0, behavior: 'instant' });
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  });
  await page.addScriptTag({ content: axe.source });
  return page.evaluate(
    async ({ context, options }: { context: string | undefined; options: RunOptions }) => {
      // `window.axe` is the global the injected script just defined; axe-core ships no typing for
      // the browser copy it injects, so this is the boundary where the shape is known.
      const axeGlobal = window as typeof window & { axe: AxeGlobal };
      // `context` is a CSS selector string here; axe resolves it inside the page.
      return axeGlobal.axe.run((context ?? document) as Document, options);
    },
    { context, options: AXE_RUN_OPTIONS },
  );
}

/** One axe finding, trimmed to what a report can quote. */
export interface AxeFinding {
  readonly id: string;
  readonly impact: string | null;
  /** axe's selector list; a frame-crossing selector is itself an array, so the shape stays open. */
  readonly targets: readonly unknown[];
  readonly summaries: readonly string[];
}

/** The violations of a scan, in the same shape the gallery's jsdom spec reports them. */
export function axeViolations(results: AxeResults): AxeFinding[] {
  return results.violations.map((violation) => ({
    id: violation.id,
    impact: violation.impact ?? null,
    targets: violation.nodes.map((node) => node.target),
    summaries: violation.nodes.map((node) => node.failureSummary ?? ''),
  }));
}

/**
 * Asserts a page is WCAG 2.2 A/AA clean — meaning no violation that `e2e/a11y-exceptions.json` does
 * not cover — AND that the colour-contrast rule actually produced an outcome (a pass or an
 * "incomplete"): a silent rule would make "no violations" meaningless.
 */
export async function expectNoA11yViolations(page: Page, context: string): Promise<void> {
  const results = await axeScan(page);
  const contrast = [...results.passes, ...results.incomplete].filter(
    (rule) => rule.id === 'color-contrast',
  );
  expect(
    contrast.length,
    `axe evaluated color-contrast on ${context} (violations: ${results.violations.length})`,
  ).toBeGreaterThan(0);
  const found = formatViolations(unexceptedViolations(results.violations, A11Y_EXCEPTIONS));
  expect(found, `axe violations on ${context}`).toEqual([]);
}
