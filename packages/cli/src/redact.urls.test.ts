import { beforeEach, describe, expect, it, vi } from 'vitest';

import { redact, resetSecrets } from './redact.js';

// A URL credential travels in more spellings than the plain http one: a scheme-relative
// reference, the percent-encoded form a query round-trip produces, a database URL, and a
// password that kept a raw space. Each row keeps the host and the path readable.
describe('URL credential spellings', () => {
  beforeEach(() => {
    resetSecrets();
  });

  const credentialRows = [
    {
      shape: 'a scheme-relative URL',
      input: 'see //user:secret@store.test/path now',
      expected: 'see //store.test/path now',
    },
    {
      shape: 'a percent-encoded URL in upper case',
      input: 'https%3A%2F%2Fuser%3Asecret%40store.test/object',
      expected: 'https%3A%2F%2Fstore.test/object',
    },
    {
      shape: 'a percent-encoded URL in lower case',
      input: 'https%3a%2f%2fuser%3asecret%40store.test/object',
      expected: 'https%3a%2f%2fstore.test/object',
    },
    {
      shape: 'a percent-encoded scheme-relative reference',
      input: '%2f%2fuser%3Asecret%40store.test/object',
      expected: '%2f%2fstore.test/object',
    },
    {
      shape: 'a postgres scheme',
      input: 'connect postgres://user:secret@db.test:5432/app now',
      expected: 'connect postgres://db.test:5432/app now',
    },
    {
      shape: 'any other scheme',
      input: 'redis://user:secret@cache.test/0',
      expected: 'redis://cache.test/0',
    },
    {
      shape: 'a raw space inside the password',
      input: 'https://user:pa ss@store.test/object',
      expected: 'https://store.test/object',
    },
    {
      shape: 'a raw space inside the password of a signed URL',
      input: 'https://user:pa ss@store.test/object?X-Amz-Signature=abc',
      expected: 'https://store.test/object?[REDACTED]',
    },
    {
      shape: 'several spaces inside the password',
      input: 'https://user:one two three@store.test/object',
      expected: 'https://store.test/object',
    },
    {
      shape: 'a numeric password with a space',
      input: 'https://user:123 456@store.test/object',
      expected: 'https://store.test/object',
    },
    {
      shape: 'a numeric password',
      input: 'postgres://svc:123@db.test:5432/app',
      expected: 'postgres://db.test:5432/app',
    },
    {
      shape: 'a quote inside the password',
      input: "postgres://svc:pa'ss@db.test:5432/app",
      expected: 'postgres://db.test:5432/app',
    },
    {
      shape: 'a fragment marker inside the password',
      input: 'postgres://svc:pa#ss@db.test:5432/app',
      expected: 'postgres://db.test:5432/app',
    },
    {
      shape: 'a query marker inside the password',
      input: 'postgres://svc:pa?ss@db.test:5432/app',
      expected: 'postgres://db.test:5432/app',
    },
    {
      shape: 'a double quote inside the password',
      input: 'postgres://svc:pa"ss@db.test:5432/app',
      expected: 'postgres://db.test:5432/app',
    },
    {
      shape: 'a password that closes at the last `@`',
      input: 'https://user:pa@ss@store.test/object',
      expected: 'https://store.test/object',
    },
    {
      shape: 'a userinfo longer than the old scan window',
      input: `//user:${'x'.repeat(507)}@host/path`,
      expected: '//host/path',
    },
    {
      shape: 'a JWT longer than the old scan window',
      input: `https://eyJ${'a'.repeat(597)}@store.test/object`,
      expected: 'https://store.test/object',
    },
  ];

  it.each(credentialRows)('strips the credential from $shape', ({ input, expected }) => {
    expect(redact(input)).toBe(expected);
  });

  it.each(credentialRows)(
    'strips the credential twice without changing the second result for $shape',
    ({ input, expected }) => {
      expect(redact(redact(input))).toBe(redact(expected));
    },
  );

  // The scan runs over every line, so ordinary text must survive it unchanged.
  const untouchedRows = [
    { shape: 'a scheme-relative URL without userinfo', text: 'see //example.com/path' },
    { shape: 'percent-encoded ordinary text', text: 'a%20b' },
    { shape: 'an email address', text: 'write to user@host' },
    { shape: 'a URL without userinfo', text: 'see https://docs.evidgate.dev/cli' },
    { shape: 'prose that follows a host and port', text: 'https://host:8080 and user@mail' },
    { shape: 'an authority that a line break ends', text: 'see //host\nuser:pw@mail' },
    { shape: 'a base64 string that is not registered', text: 'cGFzc3dvcmQ=' },
    {
      shape: 'a dotted host that a colon and a space follow',
      text: 'Read https://docs.test: contact@example.com',
    },
    { shape: 'a localhost port and the prose after it', text: 'http://localhost:3000, foo@bar' },
    {
      shape: 'a host, a port and a user-looking path',
      text: 'see https://example.test:8080/users@corp.test now',
    },
  ];

  it.each(untouchedRows)('keeps $shape intact', ({ text }) => {
    expect(redact(text)).toBe(text);
  });

  it('stays linear on 200 KB of authority-shaped text', () => {
    const credentialed = '//user:pa ss@host/path '.repeat(8_000);
    const slashes = '//'.repeat(100_000);
    const started = performance.now();

    expect(redact(credentialed)).toBe('//host/path '.repeat(8_000));
    expect(redact(slashes)).toBe(slashes);
    // Linear in the input: a backtracking shape would need seconds for either string.
    expect(performance.now() - started).toBeLessThan(1_000);
  });

  // A server message is untrusted input, so the scan must survive a megabyte of the shape that
  // is worst for it: a match at every position. Each case redacts in one pass over the text,
  // because a match advances past the run it consumed instead of rescanning it.
  const hostileRows = [
    { shape: 'percent-encoded authority starts', unit: '%2f%2f', stripped: '', repeats: 174_763 },
    {
      shape: 'a userinfo at every authority start',
      unit: '//a@',
      stripped: 'a@',
      repeats: 262_144,
    },
    {
      shape: 'a userinfo at every encoded authority start',
      unit: '%2f%2fa@',
      stripped: 'a@',
      repeats: 116_509,
    },
  ];

  // Process CPU time, not wall clock: a busy machine delays both sizes alike, so the ratio
  // stays a property of the scan, where a suffix rescan inside the match loop turns the
  // expected 4x into about 16x. The best of three runs drops collection pauses.
  const cpuMicros = (): number => {
    const usage = process.cpuUsage();
    return usage.user + usage.system;
  };

  const measureMicros = (input: string): number => {
    let best = Number.POSITIVE_INFINITY;
    for (let run = 0; run < 3; run += 1) {
      const started = cpuMicros();
      redact(input);
      best = Math.min(best, cpuMicros() - started);
    }
    return best;
  };

  it.each(hostileRows)(
    'redacts a megabyte of $shape without a rescan',
    ({ unit, stripped, repeats }) => {
      const text = unit.repeat(repeats);
      const expected = (stripped === '' ? unit : unit.replace(stripped, '')).repeat(repeats);
      const matchAll = RegExp.prototype[Symbol.matchAll];
      let lastStart = -1;
      let starts = 0;

      // Counted progress, not elapsed time: load cannot change the order of matches, while a
      // rescan that restarts at an earlier authority start repeats one. Throwing there stops a
      // regression at once instead of letting it walk the text quadratically.
      const countStarts = vi.spyOn(RegExp.prototype, Symbol.matchAll).mockImplementation(function (
        this: RegExp,
        input: string,
      ) {
        const matches = matchAll.call(this, input);
        const step = matches.next.bind(matches);
        matches.next = () => {
          const result = step();
          if (!result.done) {
            const start = result.value.index;
            if (start <= lastStart) {
              throw new Error('authority scan restarted over an earlier authority start');
            }
            lastStart = start;
            starts += 1;
          }
          return result;
        };
        return matches;
      });
      let output: string;
      try {
        output = redact(text);
      } finally {
        countStarts.mockRestore();
      }

      expect(output).toBe(expected);
      expect(starts).toBeGreaterThan(0);

      const quarter = text.slice(0, Math.floor(repeats / 4) * unit.length);
      const quarterMicros = measureMicros(quarter);
      const fullMicros = measureMicros(text);

      // Linear work scores about 4x on four times the input, quadratic about 16x; the floor keeps
      // a sub-100us baseline from turning scheduling noise into a ratio.
      expect(fullMicros).toBeLessThanOrEqual(10 * Math.max(quarterMicros, 100));
    },
  );
});
