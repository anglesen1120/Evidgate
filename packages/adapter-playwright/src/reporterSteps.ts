// Step and duration helpers for `test.step` envelopes. The walk is lazy so a deep step tree
// is appended step by step instead of materialised, which keeps the writer's working set at
// its flush threshold rather than at the size of the whole tree.
import type { TestStep } from '@playwright/test/reporter';

/**
 * Step categories whose `title` is authored by the test writer. Playwright's own `pw:api`
 * and `expect` titles embed call parameters (a filled value, a URL with its query string),
 * so only these categories may forward `title` into `ext`.
 */
const USER_AUTHORED_STEP_CATEGORIES: Readonly<Record<string, true>> = {
  'test.step': true,
  hook: true,
  fixture: true,
};

export function isUserAuthoredStepCategory(category: unknown): boolean {
  return typeof category === 'string' && USER_AUTHORED_STEP_CATEGORIES[category] === true;
}

/**
 * Depth-first (parent before child) walk of the nested `result.steps` tree. Playwright's
 * `TestStep.steps` is a nested array, so a recursive walk is what emits every step.
 */
export function* walkSteps(steps: readonly TestStep[]): Generator<TestStep> {
  for (const step of steps) {
    yield step;
    yield* walkSteps(step.steps);
  }
}

/**
 * The `duration_ms` member for an envelope, or `{}` when the value is not a non-negative
 * finite number. The envelope schema requires an integer >= 0 and Playwright initialises an
 * unfinished step's duration to -1; non-integers are rounded.
 */
export function durationField(value: unknown): { duration_ms?: number } {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return {};
  return { duration_ms: Math.round(value) };
}
