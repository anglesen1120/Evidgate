// 04.3.1-T1.3 / 04.3.2-T1.2: three browserless tests (no `page` fixture) so the e2e run
// needs no browser download and stays fast. One test wraps its assertion in a user
// `test.step` so reporter.e2e.test.ts can assert a real `test.step` envelope (category
// `test.step`, integer `duration_ms`) produced from real Playwright objects. Each test
// carries an `evidgate.id` annotation (04.3.2 AC1) so the reporter's built-in resolver
// maps it to `ext.case_id` end to end.
import { expect, test } from '@playwright/test';

test(
  'adds two numbers',
  { annotation: { type: 'evidgate.id', description: '11111111-1111-1111-1111-111111111111' } },
  () => {
    expect(1 + 1).toBe(2);
  },
);

test(
  'concatenates strings',
  { annotation: { type: 'evidgate.id', description: '22222222-2222-2222-2222-222222222222' } },
  () => {
    test.step('concatenate', () => {
      expect('a' + 'b').toBe('ab');
    });
  },
);

test(
  'compares booleans',
  { annotation: { type: 'evidgate.id', description: '33333333-3333-3333-3333-333333333333' } },
  () => {
    expect(true).toBe(true);
  },
);
