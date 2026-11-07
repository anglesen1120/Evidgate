// Axe runs at two levels — the Vitest component specs and the Playwright core-flow specs — against
// one tag set and one exception list, so a waiver cannot exist at one level and be ignored at the
// other. An exception is data keyed on (rule id, selector): it stops covering a node as soon as the
// rule or the element changes, and no rule is ever disabled globally.
import type { RunOptions } from 'axe-core';

/** Everything the product claims for WCAG 2.2 AA, colour contrast included. */
export const AXE_WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] as const;

/** The options a real-browser run uses: the engine has layout and colours, so every rule is live. */
export const AXE_RUN_OPTIONS: RunOptions = {
  runOnly: { type: 'tag', values: [...AXE_WCAG_TAGS] },
};

/**
 * The options a jsdom run must use: jsdom computes no layout and no colours, so contrast would
 * report one inconclusive node per element. Contrast is measured from the recorded token pairs in
 * `docs/design/web/tokens.md#contrast-table` and in the real browser, never here.
 */
export const AXE_JSDOM_RUN_OPTIONS: RunOptions = {
  ...AXE_RUN_OPTIONS,
  rules: { 'color-contrast': { enabled: false } },
};

/**
 * One accepted violation. All five fields are required — a missing one fails the gate — and
 * `review-by` is capped by the review cadence, so an entry expires instead of becoming permanent.
 */
export interface A11yException {
  readonly rule: string;
  readonly selector: string;
  readonly reason: string;
  readonly owner: string;
  /** Last day the entry may stay: `YYYY-MM-DD`, at most 90 days after it was added. */
  readonly 'review-by': string;
}

/** One reported element of a violation, as much of it as the matching needs. */
export interface AxeViolationNodeLike {
  readonly target: unknown;
  readonly failureSummary?: string | null;
}

/** A violation in the shape both axe results and a fixture already have. */
export interface AxeViolationLike {
  readonly id: string;
  readonly impact?: string | null;
  readonly nodes: readonly AxeViolationNodeLike[];
}

/** A violation that survived the exception list, holding only its unexcepted nodes. */
export interface UnexceptedViolation {
  readonly id: string;
  readonly impact: string | null;
  readonly nodes: readonly AxeViolationNodeLike[];
}

/** Axe composes selectors and summaries with newlines and padding; one line is what a report has. */
function oneLine(text: string): string {
  return text.trim().replace(/\s+/g, ' ');
}

/** `Array.isArray` narrows to `any[]`; this keeps the element type unknown for the recursion. */
function isSelectorChain(value: unknown): value is readonly unknown[] {
  return Array.isArray(value);
}

/**
 * The element a reported node names. Axe answers a selector, or — inside frames — a chain whose
 * last entry is the selector of the deepest frame, which is where the element actually is.
 */
export function targetSelector(target: unknown): string {
  if (typeof target === 'string') return oneLine(target);
  if (!isSelectorChain(target)) return '';
  for (let index = target.length - 1; index >= 0; index -= 1) {
    const selector = targetSelector(target[index]);
    if (selector !== '') return selector;
  }
  return '';
}

/**
 * Whether one entry's selector names the reported element: the exact target, or the path it ends
 * with at a step boundary. A bare leaf therefore waives every element with that leaf, which is why
 * the findings list asks for the most specific path the failure message prints.
 */
function namesTarget(selector: string, target: string): boolean {
  const wanted = oneLine(selector);
  if (wanted === '') return false;
  return target === wanted || target.endsWith(` > ${wanted}`) || target.endsWith(` ${wanted}`);
}

/** Whether an exception covers one reported node: same rule id AND the same element. */
export function isExcepted(
  exceptions: readonly A11yException[],
  ruleId: string,
  target: unknown,
): boolean {
  const selector = targetSelector(target);
  if (selector === '') return false;
  return exceptions.some((entry) => entry.rule === ruleId && namesTarget(entry.selector, selector));
}

/**
 * The violations still worth failing on. Exceptions are applied per node, not per rule: a rule
 * reported on three elements with one exception still fails on the other two.
 */
export function unexceptedViolations(
  violations: readonly AxeViolationLike[],
  exceptions: readonly A11yException[],
): UnexceptedViolation[] {
  const remaining: UnexceptedViolation[] = [];
  for (const violation of violations) {
    const nodes = violation.nodes.filter(
      (node) => !isExcepted(exceptions, violation.id, node.target),
    );
    if (nodes.length > 0) {
      remaining.push({ id: violation.id, impact: violation.impact ?? null, nodes });
    }
  }
  return remaining;
}

/** One line per reported node, so a failure names the rule, the impact and the element. */
export function formatViolations(violations: readonly UnexceptedViolation[]): string[] {
  const lines: string[] = [];
  for (const violation of violations) {
    const impact = violation.impact === null ? '' : ` (${violation.impact})`;
    for (const node of violation.nodes) {
      const summary = oneLine(node.failureSummary ?? '');
      const detail = summary === '' ? '' : `: ${summary}`;
      lines.push(`${violation.id}${impact} at ${targetSelector(node.target)}${detail}`);
    }
  }
  return lines;
}
