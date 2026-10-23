// 04.3.2-T1.2: a single test with no `evidgate.id` annotation, mapped by the sidecar
// manifest next to it (evidgate.cases.yaml). reporter.e2e.test.ts asserts its envelopes
// carry the sidecar case id, proving the sidecar path end to end.
import { expect, test } from '@playwright/test';

test('sidecar mapped test', () => {
  expect(2 + 2).toBe(4);
});
