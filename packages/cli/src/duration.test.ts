import { describe, expect, it } from 'vitest';

import { parseDuration, type DurationRejection, type ParsedDuration } from './duration.js';

/** A ceiling wide enough that only the value under test can refuse the parse. */
const wideCeiling = Number.MAX_SAFE_INTEGER;

function rejected(reason: DurationRejection): ParsedDuration {
  return { ok: false, reason };
}

describe('parseDuration', () => {
  const accepted: ReadonlyArray<readonly [string, number]> = [
    ['1', 1_000],
    ['90', 90_000],
    ['1s', 1_000],
    ['90s', 90_000],
    ['2m', 120_000],
    ['1h', 3_600_000],
    ['24h', 86_400_000],
    ['00000000000000000001', 1_000],
  ];

  for (const [raw, milliseconds] of accepted) {
    it(`parses ${JSON.stringify(raw)} as ${milliseconds} ms`, () => {
      expect(parseDuration(raw, wideCeiling)).toStrictEqual({ ok: true, milliseconds });
    });
  }

  const grammar: readonly string[] = [
    '',
    's',
    'm',
    'h',
    '-5',
    '+5',
    '1.5',
    '1.5m',
    '1d',
    '1S',
    's1',
    ' 1s',
    '1s ',
    '1 s',
    '1_000',
    '0x10',
    '100000000000000000000',
  ];

  for (const raw of grammar) {
    it(`refuses ${JSON.stringify(raw)} as grammar`, () => {
      expect(parseDuration(raw, wideCeiling)).toStrictEqual(rejected('grammar'));
    });
  }

  const notPositive: readonly string[] = ['0', '0s', '0m', '0h', '000', '00000000000000000000'];

  for (const raw of notPositive) {
    it(`refuses ${JSON.stringify(raw)} as not positive`, () => {
      expect(parseDuration(raw, wideCeiling)).toStrictEqual(rejected('not_positive'));
    });
  }

  it('accepts a value exactly at the ceiling and refuses one millisecond above it', () => {
    expect(parseDuration('10', 10_000)).toStrictEqual({ ok: true, milliseconds: 10_000 });
    expect(parseDuration('11', 10_000)).toStrictEqual(rejected('above_ceiling'));
  });

  it('refuses a product outside the safe integer range even under a wide ceiling', () => {
    // 9007199254740 s still fits; one more second is no longer exactly representable.
    expect(parseDuration('9007199254740', wideCeiling)).toStrictEqual({
      ok: true,
      milliseconds: 9_007_199_254_740_000,
    });
    expect(parseDuration('9007199254741', wideCeiling)).toStrictEqual(rejected('above_ceiling'));
    expect(parseDuration('99999999999999999999', wideCeiling)).toStrictEqual(
      rejected('above_ceiling'),
    );
  });

  it('refuses a zero ceiling without a positive value slipping through', () => {
    expect(parseDuration('1', 0)).toStrictEqual(rejected('above_ceiling'));
    expect(parseDuration('0', 0)).toStrictEqual(rejected('not_positive'));
  });
});
