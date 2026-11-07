// The usage-summary fixture documents, checked for the rule they exist to prove: a counter with
// no measurement stays null (rendered "unavailable"), never a zero that reads as a measurement,
// and an empty summary is an empty list rather than an unavailable one.

import type { UsageSummaryDto } from '@evidgate/sdk';

import { findUsageSummaryFixture, USAGE_SUMMARY_VARIANTS } from './fixture-usage-summary';

/** The 200 body of one variant, narrowed for the assertions. */
function bodyOf(variant: string): UsageSummaryDto {
  const entry = findUsageSummaryFixture(variant);
  if (entry === undefined || entry.document.status !== 200) {
    throw new Error(`expected a served summary for ${variant}`);
  }
  return entry.document.body as UsageSummaryDto;
}

describe('usage summary fixtures', () => {
  it('serves every declared variant as the getUsageSummary document', () => {
    for (const variant of USAGE_SUMMARY_VARIANTS) {
      const entry = findUsageSummaryFixture(variant);
      expect(entry?.document.operation_id).toBe('getUsageSummary');
      expect(entry?.document.variant).toBe(variant);
      expect(entry?.name).toBe(`usage-summary-${variant}`);
    }
  });

  it('answers an unknown variant with nothing, so the backend says fixture_not_found', () => {
    expect(findUsageSummaryFixture('warned')).toBeUndefined();
    expect(findUsageSummaryFixture('')).toBeUndefined();
  });

  it('never gives an unmeasured counter a number or a limit', () => {
    for (const variant of ['ok', 'warn', 'blocked', 'empty', 'unavailable']) {
      for (const metric of bodyOf(variant).metrics) {
        if (metric.used === null) {
          expect(metric.limit).toBeNull();
          expect(metric.unavailable_reason).not.toBeNull();
        }
      }
    }
  });

  it('records the warning and the block on the results counter only', () => {
    expect(bodyOf('ok').metrics.every((metric) => metric.warned_at === null)).toBe(true);

    const warn = bodyOf('warn').metrics;
    expect(
      warn.filter((metric) => metric.warned_at !== null).map((metric) => metric.metric),
    ).toEqual(['results']);
    expect(warn.find((metric) => metric.metric === 'results')?.blocked_at).toBeNull();

    const blocked = bodyOf('blocked').metrics.find((metric) => metric.metric === 'results');
    expect(blocked?.blocked_at).not.toBeNull();
    expect(blocked?.retry_after_seconds).toBeGreaterThan(0);
  });

  it('separates an empty summary from an unavailable one', () => {
    expect(bodyOf('empty').metrics).toEqual([]);

    const egress = bodyOf('unavailable').metrics.find((metric) => metric.metric === 'egress_gb');
    expect(egress?.used).toBeNull();
    expect(egress?.unavailable_reason).toContain('egress_bytes');
    // The rest of the summary is served: one unavailable metric does not make the whole read fail.
    expect(
      bodyOf('unavailable').metrics.filter((metric) => metric.used !== null).length,
    ).toBeGreaterThan(0);
  });

  it('serves the error variant as a 503 envelope the mapper reads as unavailable', () => {
    const entry = findUsageSummaryFixture('error');
    expect(entry?.document.status).toBe(503);
    expect(entry?.document.headers['Retry-After']).toBe('5');
    const body = entry?.document.body as { error: { code: string; retryable: boolean } };
    expect(body.error.code).toBe('unavailable');
    expect(body.error.retryable).toBe(true);
  });
});
