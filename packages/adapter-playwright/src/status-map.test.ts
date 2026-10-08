// 04.3.2-T2.1/T2.2: the pure status mapping (AC6) — every AC6 row, xfail, skip vs
// fixme precedence, unknown future statuses, the 64-char raw cut (envelope `status`
// maxLength), `errorSummary` (first line only, never a stack frame) and `attemptOf` (AC5).
// The table is the contract in contracts/adapter/playwright-mapping.md §status.
import { describe, expect, it } from 'vitest';

import {
  ERROR_SUMMARY_MAX,
  STATUS_TABLE,
  attemptOf,
  errorSummary,
  mapStatus,
  printable,
  type StatusInput,
  type StatusMapping,
} from './status.js';

type Annotation = StatusInput['annotations'][number];

const SKIP: Annotation = { type: 'skip' };
const FIXME: Annotation = { type: 'fixme', description: 'flaky on CI' };

function input(
  status: string,
  annotations: readonly Annotation[] = [],
  expectedStatus?: string,
): StatusInput {
  return expectedStatus === undefined
    ? { status, annotations }
    : { status, annotations, expectedStatus };
}

describe('mapStatus — AC6 table', () => {
  // [name, input, normalized, skip_reason, xfail]
  const rows: Array<
    [string, StatusInput, StatusMapping['normalized'], 'fixme' | undefined, true | undefined]
  > = [
    ['passed', input('passed'), 'passed', undefined, undefined],
    ['failed', input('failed'), 'failed', undefined, undefined],
    ['timedOut', input('timedOut'), 'timed_out', undefined, undefined],
    ['interrupted', input('interrupted'), 'error', undefined, undefined],
    ['skipped + skip annotation', input('skipped', [SKIP]), 'skipped', undefined, undefined],
    ['skipped + fixme annotation', input('skipped', [FIXME]), 'skipped', 'fixme', undefined],
    ['skipped + skip then fixme', input('skipped', [SKIP, FIXME]), 'skipped', 'fixme', undefined],
    ['skipped + fixme then skip', input('skipped', [FIXME, SKIP]), 'skipped', 'fixme', undefined],
    ['skipped, no annotation', input('skipped'), 'skipped', undefined, undefined],
    [
      'skipped + unrelated annotations only',
      input('skipped', [{ type: 'evidgate.id', description: 'x' }, { type: 'issue' }]),
      'skipped',
      undefined,
      undefined,
    ],
    ['expected failed × failed (xfail)', input('failed', [], 'failed'), 'passed', undefined, true],
    [
      'expected failed × passed (unexpected pass, fail closed)',
      input('passed', [], 'failed'),
      'failed',
      undefined,
      true,
    ],
    [
      'expected failed × timedOut',
      input('timedOut', [], 'failed'),
      'timed_out',
      undefined,
      undefined,
    ],
    [
      'expected failed × interrupted',
      input('interrupted', [], 'failed'),
      'error',
      undefined,
      undefined,
    ],
    ['expected failed × skipped', input('skipped', [], 'failed'), 'skipped', undefined, undefined],
    ['expected passed × failed', input('failed', [], 'passed'), 'failed', undefined, undefined],
    [
      'expected skipped × skipped',
      input('skipped', [], 'skipped'),
      'skipped',
      undefined,
      undefined,
    ],
  ];

  it.each(rows)('%s', (_name, given, normalized, skipReason, xfail) => {
    const mapping = mapStatus(given);
    expect(mapping.raw).toBe(given.status);
    expect(mapping.normalized).toBe(normalized);
    expect(mapping.skip_reason).toBe(skipReason);
    expect(mapping.xfail).toBe(xfail);
    expect(mapping.unknown).toBeUndefined();
    expect(mapping.error_summary).toBeUndefined();
  });

  it('the table maps exactly the five Playwright 1.63 statuses', () => {
    expect({ ...STATUS_TABLE }).toEqual({
      passed: 'passed',
      failed: 'failed',
      timedOut: 'timed_out',
      interrupted: 'error',
      skipped: 'skipped',
    });
  });

  it('interrupted and timedOut stay distinct in raw and normalized', () => {
    const interrupted = mapStatus(input('interrupted'));
    const timedOut = mapStatus(input('timedOut'));
    expect(interrupted.raw).not.toBe(timedOut.raw);
    expect(interrupted.normalized).toBe('error');
    expect(timedOut.normalized).toBe('timed_out');
  });

  it('never sets skip_reason on a non-skipped status, even with a fixme annotation', () => {
    for (const status of ['passed', 'failed', 'timedOut', 'interrupted', 'flaky-future']) {
      expect(mapStatus(input(status, [FIXME])).skip_reason).toBeUndefined();
    }
    expect(mapStatus(input('failed', [FIXME], 'failed')).skip_reason).toBeUndefined();
  });

  it('is pure: the same input maps to the same output and is not mutated', () => {
    const annotations = Object.freeze([Object.freeze({ ...FIXME })]);
    const given = Object.freeze({ status: 'skipped', annotations });
    expect(mapStatus(given)).toEqual(mapStatus(given));
    expect(given.status).toBe('skipped');
  });
});

describe('mapStatus — unknown future statuses', () => {
  it.each([
    'flaky-future',
    '',
    'constructor',
    '__proto__',
    'toString',
    'PASSED',
    'timedout',
    ' passed',
  ])('%j → error + unknown, raw verbatim', (status) => {
    const mapping = mapStatus(input(status));
    expect(mapping.raw).toBe(status);
    expect(mapping.normalized).toBe('error');
    expect(mapping.unknown).toBe(true);
    expect(mapping.skip_reason).toBeUndefined();
    expect(mapping.xfail).toBeUndefined();
  });

  it('an unknown status with expectedStatus failed is not an xfail', () => {
    const mapping = mapStatus(input('flaky-future', [], 'failed'));
    expect(mapping.normalized).toBe('error');
    expect(mapping.unknown).toBe(true);
    expect(mapping.xfail).toBeUndefined();
  });

  it('cuts a raw status longer than 64 chars to 64 (envelope status maxLength)', () => {
    const long = `future-${'x'.repeat(93)}`;
    const mapping = mapStatus(input(long));
    expect(mapping.raw).toBe(long.slice(0, 64));
    expect(mapping.raw).toHaveLength(64);
    expect(mapping.normalized).toBe('error');
    expect(mapping.unknown).toBe(true);
  });

  it('keeps a raw status of exactly 64 chars whole', () => {
    const exact = 'y'.repeat(64);
    expect(mapStatus(input(exact)).raw).toBe(exact);
  });

  it('never leaves a lone high surrogate at the 64-char raw cut', () => {
    const raw = mapStatus(input(`${'a'.repeat(63)}\u{1F600}`)).raw;
    expect(raw).toBe('a'.repeat(63));
  });
});

describe('mapStatus — error_summary', () => {
  it('is present for a failed attempt and holds the first line only', () => {
    const mapping = mapStatus({
      status: 'failed',
      annotations: [],
      errors: [{ message: 'Error: expect(received).toBe(expected)\n\nExpected: 2\nReceived: 3' }],
    });
    expect(mapping.error_summary).toBe('Error: expect(received).toBe(expected)');
  });

  it('is present on an xfail pass too', () => {
    const mapping = mapStatus({
      status: 'failed',
      expectedStatus: 'failed',
      annotations: [],
      errors: [{ message: 'boom\n    at Object.<anonymous> (tests/a.spec.ts:4:13)' }],
    });
    expect(mapping.normalized).toBe('passed');
    expect(mapping.xfail).toBe(true);
    expect(mapping.error_summary).toBe('boom');
  });

  it('is absent when errors carry no text', () => {
    expect(
      mapStatus({ status: 'failed', annotations: [], errors: [] }).error_summary,
    ).toBeUndefined();
    expect(mapStatus({ status: 'failed', annotations: [] }).error_summary).toBeUndefined();
  });
});

describe('errorSummary', () => {
  it('is absent for [] and undefined', () => {
    expect(errorSummary([])).toBeUndefined();
    expect(errorSummary(undefined)).toBeUndefined();
  });

  it('takes only the first line and never a stack frame', () => {
    const summary = errorSummary([
      {
        message:
          'TimeoutError: locator.click: Timeout 5000ms exceeded.\n' +
          '    at /repo/tests/login.spec.ts:12:7\n' +
          '    at processTicksAndRejections (node:internal/process/task_queues:95:5)',
      },
    ]);
    expect(summary).toBe('TimeoutError: locator.click: Timeout 5000ms exceeded.');
    expect(summary).not.toMatch(/\n|\r|\bat \//);
  });

  it.each([
    ['LF', 'first\nsecond'],
    ['CRLF', 'first\r\nsecond'],
    ['CR', 'first\rsecond'],
  ])('splits on %s', (_name, message) => {
    expect(errorSummary([{ message }])).toBe('first');
  });

  it('trims the first line', () => {
    expect(errorSummary([{ message: '   padded message \t\nnext' }])).toBe('padded message');
  });

  it('strips ANSI CSI sequences', () => {
    const message =
      '\u001b[2mexpect(\u001b[22m\u001b[31mreceived\u001b[39m\u001b[2m).\u001b[22mtoBe\u001b[2m(\u001b[22m\u001b[32mexpected\u001b[39m\u001b[2m)\u001b[22m\n\nExpected: \u001b[32m2\u001b[39m';
    const summary = errorSummary([{ message }]);
    expect(summary).toBe('expect(received).toBe(expected)');
    expect(summary).not.toContain('\u001b');
  });

  it('strips ANSI OSC sequences (hyperlinks)', () => {
    const message =
      'see \u001b]8;;https://example.test/doc\u0007the docs\u001b]8;;\u0007 for details';
    const summary = errorSummary([{ message }]);
    expect(summary).toBe('see the docs for details');
    expect(summary).not.toContain('\u001b');
    expect(summary).not.toContain('\u0007');
  });

  it('uses value when message is absent', () => {
    expect(errorSummary([{ value: 'thrown string\nsecond line' }])).toBe('thrown string');
  });

  it('prefers message over value', () => {
    expect(errorSummary([{ message: 'from message', value: 'from value' }])).toBe('from message');
  });

  it('skips leading errors whose first line is empty', () => {
    expect(
      errorSummary([
        {},
        { message: '' },
        { message: '   ' },
        { message: '\u001b[31m\u001b[39m' },
        { message: '\n    at /repo/tests/a.spec.ts:1:1' },
        { value: 'the real one\n    at x' },
        { message: 'later error' },
      ]),
    ).toBe('the real one');
  });

  it('never falls back to a stack property', () => {
    const errors = [
      { stack: 'Error: hidden\n    at /repo/tests/a.spec.ts:1:1' },
    ] as unknown as StatusInput['errors'];
    expect(errorSummary(errors)).toBeUndefined();
  });

  it(`caps at ERROR_SUMMARY_MAX (${ERROR_SUMMARY_MAX}) UTF-16 units`, () => {
    expect(ERROR_SUMMARY_MAX).toBe(256);
    expect(errorSummary([{ message: 'a'.repeat(ERROR_SUMMARY_MAX) }])).toBe(
      'a'.repeat(ERROR_SUMMARY_MAX),
    );
    expect(errorSummary([{ message: 'a'.repeat(ERROR_SUMMARY_MAX + 44) }])).toBe(
      'a'.repeat(ERROR_SUMMARY_MAX),
    );
  });

  it('never leaves a lone high surrogate at the cut', () => {
    // The emoji's high surrogate sits at index 255, its low surrogate at 256.
    const message = `${'a'.repeat(ERROR_SUMMARY_MAX - 1)}\u{1F600}tail`;
    const summary = errorSummary([{ message }]) ?? '';
    expect(summary.length).toBeLessThanOrEqual(ERROR_SUMMARY_MAX);
    expect(summary).toBe('a'.repeat(ERROR_SUMMARY_MAX - 1));
    const last = summary.charCodeAt(summary.length - 1);
    expect(last >= 0xd800 && last <= 0xdbff).toBe(false);
    // A pair that fits whole is kept.
    const fits = `${'a'.repeat(ERROR_SUMMARY_MAX - 2)}\u{1F600}tail`;
    expect(errorSummary([{ message: fits }])).toBe(`${'a'.repeat(ERROR_SUMMARY_MAX - 2)}\u{1F600}`);
  });
});

describe('attemptOf — AC5', () => {
  it.each([
    [0, 1],
    [1, 2],
    [2, 3],
  ])('retry %i → attempt_no %i', (retry, attempt) => {
    expect(attemptOf({ retry })).toBe(attempt);
  });
});

describe('errorSummary — review hardening (SEC-1, SEC-2, PERF-1)', () => {
  it.each([
    ['NEL', 'first\u0085second'],
    ['LINE SEPARATOR', 'first\u2028second'],
    ['PARAGRAPH SEPARATOR', 'first\u2029second'],
  ])('%s ends the first line', (_name, message) => {
    expect(errorSummary([{ message }])).toBe('first');
  });

  it('removes control and BIDI characters left after the ANSI strip', () => {
    const message = 'a\u001bc\u009b31m\u0000b\u0007\u202eevil\u2066x\u2069\u200b\tz';
    expect(errorSummary([{ message }])).toBe('ac31mbevilx z');
  });

  it('skips an error whose first line is only control characters', () => {
    expect(errorSummary([{ message: '\u001b\u0000\u202e' }, { message: 'real' }])).toBe('real');
  });

  it.each([
    [
      'page.goto: net::ERR_CONNECTION_REFUSED at https://app.test/login?token=abc123&x=1',
      'page.goto: net::ERR_CONNECTION_REFUSED at https://app.test/login?[redacted]',
    ],
    [
      'GET https://user:pw@api.test/v1/items failed',
      'GET https://[redacted]@api.test/v1/items failed',
    ],
    ['opened http://h.test/p#access_token=s3cr3t', 'opened http://h.test/p#[redacted]'],
    ['no url here: a?b=c', 'no url here: a?b=c'],
  ])('redacts URL userinfo, query and fragment: %s', (message, expected) => {
    expect(errorSummary([{ message }])).toBe(expected);
  });

  it('never keeps a secret query value even when the cap cuts inside the query', () => {
    const message = `${'x'.repeat(ERROR_SUMMARY_MAX - 30)} https://h.test/p?token=${'s'.repeat(50)}`;
    const summary = errorSummary([{ message }]);
    expect(summary).not.toContain('token=');
    expect(summary?.length).toBeLessThanOrEqual(ERROR_SUMMARY_MAX);
  });

  it('reads a bounded prefix of a huge multi-line message', () => {
    const message = `first line\n${'a\n'.repeat(5_000_000)}`;
    const started = performance.now();
    expect(errorSummary([{ message }])).toBe('first line');
    expect(performance.now() - started).toBeLessThan(50);
  });

  it('printable turns line terminators into spaces and drops other controls', () => {
    expect(printable('a\r\nb\u2028c\u0000d\u202e')).toBe('a b cd');
  });
});
