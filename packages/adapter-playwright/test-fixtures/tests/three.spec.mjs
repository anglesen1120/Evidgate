// 04.3.1-T1.3: three browserless tests (no `page` fixture) so the e2e run needs no
// browser download and stays fast. One test wraps its assertion in a user `test.step`
// so reporter.e2e.test.ts can assert a real `test.step` envelope (category `test.step`,
// integer `duration_ms`) produced from real Playwright objects.
import { expect, test } from '@playwright/test';

test('adds two numbers', () => {
  expect(1 + 1).toBe(2);
});

test('concatenates strings', () => {
  test.step('concatenate', () => {
    expect('a' + 'b').toBe('ab');
  });
});

test('compares booleans', () => {
  expect(true).toBe(true);
});
