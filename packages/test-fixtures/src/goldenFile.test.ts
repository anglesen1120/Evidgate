// 01.6.1-T3.2 (AC6): the golden-file helper pins RFC 8785 canonical bytes.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';

import {
  assertCanonicalGolden,
  assertGoldenEqual,
  canonicalJson,
  GoldenFileError,
  readGolden,
  sha256OfFile,
} from './goldenFile.js';

const REPO_ROOT = fileURLToPath(new URL('../../..', import.meta.url));
const SAMPLE_EXPECTED = join(REPO_ROOT, 'fixtures', 'reports', 'junit', 'sample', 'expected.json');

/** Same digest literal as FixtureLoaderTests.cs — cross-runtime byte parity. */
const SAMPLE_EXPECTED_SHA256 =
  'sha256:722cd0645ed9bc4ee524514cf4856c8e718c21beb23a2941393efacd7ee2090d';

const tempDirs: string[] = [];

function golden(content: string): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-golden-'));
  tempDirs.push(dir);
  const path = join(dir, 'expected.json');
  writeFileSync(path, content, 'utf8');
  return path;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe('canonicalJson', () => {
  it('sorts members and drops insignificant whitespace', () => {
    expect(canonicalJson({ b: 1, a: [true, null], nested: { y: 'x', i: 3 } })).toBe(
      '{"a":[true,null],"b":1,"nested":{"i":3,"y":"x"}}',
    );
  });

  it('rejects values that are not valid JSON data', () => {
    expect(() => canonicalJson(undefined)).toThrowError(TypeError);
    expect(() => canonicalJson(() => 1)).toThrowError(TypeError);
  });
});

describe('assertCanonicalGolden', () => {
  it('accepts the byte-exact sample golden', () => {
    const canonical = assertCanonicalGolden(SAMPLE_EXPECTED);
    expect(canonical).toContain('"producer":"junit"');
    expect(canonical).not.toContain('\n');
  });

  it('rejects a pretty-printed golden', () => {
    expect(() => assertCanonicalGolden(golden('{\n  "a": 1,\n  "b": 2\n}'))).toThrowError(
      /not the RFC 8785 canonical byte sequence/,
    );
  });

  it('rejects a canonical document with a trailing newline', () => {
    expect(() => assertCanonicalGolden(golden('{"a":1}\n'))).toThrowError(GoldenFileError);
  });

  it('rejects a golden that is not JSON', () => {
    expect(() => assertCanonicalGolden(golden('not json'))).toThrowError(/not valid JSON/);
  });

  it('rejects a missing golden', () => {
    expect(() => assertCanonicalGolden(join(tmpdir(), 'evidgate-no-golden.json'))).toThrowError(
      /does not exist/,
    );
  });
});

describe('assertGoldenEqual', () => {
  it('passes when the produced value canonicalizes to the golden bytes', () => {
    expect(() =>
      assertGoldenEqual(
        {
          summary: { tests: 3, failures: 1, errors: 0, skipped: 1, durationMs: 420 },
          producer: 'junit',
          events: [
            { outcome: 'passed', name: 'Case_Passes', durationMs: 100, class: 'Sample.Tests' },
            {
              outcome: 'failed',
              name: 'Case_Fails',
              durationMs: 220,
              class: 'Sample.Tests',
              message: 'expected 2 but was 1',
            },
            {
              outcome: 'skipped',
              name: 'Case_Skipped',
              durationMs: 0,
              class: 'Sample.Tests',
              message: 'not applicable',
            },
          ],
        },
        SAMPLE_EXPECTED,
      ),
    ).not.toThrowError();
  });

  it('fails with expected/actual text when the value differs', () => {
    expect(() => assertGoldenEqual({ producer: 'other' }, SAMPLE_EXPECTED)).toThrowError(
      /differs from the golden file/,
    );
  });
});

describe('readGolden + sha256OfFile', () => {
  it('parses a canonical golden', () => {
    const value = readGolden(SAMPLE_EXPECTED) as { producer: string };
    expect(value.producer).toBe('junit');
  });

  it('digests the raw bytes of the sample golden', () => {
    expect(sha256OfFile(SAMPLE_EXPECTED)).toBe(SAMPLE_EXPECTED_SHA256);
  });
});
