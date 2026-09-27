// 01.6.1-T3.2 (AC6): the TypeScript half of the loader round-trip. The same
// fixtures/reports/junit/sample bytes are loaded by
// tests/Foundation.IntegrationTests/FixtureLoaderTests.cs, which pins the same
// sha256: digest of expected.json — the byte-level proof both runtimes read one
// corpus.
import { mkdtempSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';

import { sha256OfFile } from './goldenFile.js';
import {
  FixtureShapeError,
  loadAllFixtures,
  loadFixture,
  tryLoadFixture,
  type Fixture,
} from './loadFixture.js';

/** See FixtureLoaderTests.cs — pinned on both runtimes. */
const SAMPLE_EXPECTED_SHA256 =
  'sha256:722cd0645ed9bc4ee524514cf4856c8e718c21beb23a2941393efacd7ee2090d';

const REPO_ROOT = fileURLToPath(new URL('../../..', import.meta.url));
const FIXTURES_ROOT = join(REPO_ROOT, 'fixtures');
const SAMPLE_DIR = join(FIXTURES_ROOT, 'reports', 'junit', 'sample');

const INPUT_XML = '<testsuite tests="1"></testsuite>';
const VALID_META = '{"domain":"tmp","kind":"junit","source":"handcrafted","added_by":"tests"}';

const tempDirs: string[] = [];

function makeFixtureDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-fixture-'));
  tempDirs.push(dir);
  return dir;
}

function write(dir: string, name: string, content: string): void {
  writeFileSync(join(dir, name), content, 'utf8');
}

/** Writes content with a UTF-8 BOM prefix — an unknown shape. */
function writeWithBom(dir: string, name: string, content: string): void {
  writeFileSync(
    join(dir, name),
    Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(content, 'utf8')]),
  );
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe('loadFixture', () => {
  it('round-trips the shared JUnit sample', () => {
    const fixture = loadFixture(SAMPLE_DIR);

    expect(fixture.domain).toBe('reports');
    expect(fixture.name).toBe('sample');
    expect(fixture.dir).toBe(SAMPLE_DIR);
    expect(fixture.meta).toEqual({
      domain: 'reports',
      kind: 'junit',
      source: 'handcrafted',
      addedBy: '01.6.1-T3.2',
    });
    expect(fixture.inputPath.endsWith('input.xml')).toBe(true);
    expect(fixture.inputText).toContain('<testsuite');
    expect(fixture.inputText).toContain('tests="3"');

    // expected.json is a plain-object typed `unknown`; the sample's shape is
    // pinned by the assertions below (04 owns the real normalisation contract).
    const expected = fixture.expected as {
      producer: string;
      summary: {
        tests: number;
        failures: number;
        errors: number;
        skipped: number;
        durationMs: number;
      };
      events: Array<{ outcome: string; name: string; message?: string }>;
    };
    expect(expected.producer).toBe('junit');
    expect(expected.summary).toEqual({
      durationMs: 420,
      errors: 0,
      failures: 1,
      skipped: 1,
      tests: 3,
    });
    expect(expected.events.map((event) => event.outcome)).toEqual(['passed', 'failed', 'skipped']);
    expect(expected.events[1]!.message).toBe('expected 2 but was 1');

    // Canonical expected.json has no insignificant whitespace (raw newlines in
    // strings are escaped), so a single line is a necessary condition; the
    // byte-exact assertion lives in goldenFile's canonical check.
    expect(fixture.expectedText).not.toContain('\n');

    // Cross-runtime byte parity: the digest the .NET loader test pins.
    expect(sha256OfFile(join(SAMPLE_DIR, 'expected.json'))).toBe(SAMPLE_EXPECTED_SHA256);
  });

  it('loads a fixture without expected.json (it is optional)', () => {
    const dir = makeFixtureDir();
    write(dir, 'meta.json', VALID_META);
    write(dir, 'input.xml', INPUT_XML);

    const fixture = loadFixture(dir);
    expect(fixture.domain).toBe('tmp');
    expect(fixture.inputText).toBe(INPUT_XML);
    expect(fixture.expected).toBeUndefined();
    expect(fixture.expectedText).toBeUndefined();
  });

  it('rejects an expected.json that is valid JSON but not canonical bytes', () => {
    const dir = makeFixtureDir();
    write(dir, 'meta.json', VALID_META);
    write(dir, 'input.xml', INPUT_XML);
    write(dir, 'expected.json', '{\n  "a": 1\n}\n');

    expect(() => loadFixture(dir)).toThrowError(FixtureShapeError);
    expect(() => loadFixture(dir)).toThrowError(
      /expected\.json must be the RFC 8785 canonical byte sequence/,
    );
  });

  it('does not mistake duplicate-shaped text inside a member value for a duplicate name', () => {
    const dir = makeFixtureDir();
    write(
      dir,
      'meta.json',
      '{"domain":"tmp","kind":"junit","source":"{\\"n\\":1,\\"n\\":2}","added_by":"tests"}',
    );
    write(dir, 'input.xml', INPUT_XML);

    const fixture = loadFixture(dir);
    expect(fixture.meta.source).toBe('{"n":1,"n":2}');
  });

  const refusals: Array<{ name: string; populate: (dir: string) => void; problem: RegExp }> = [
    {
      name: 'missing meta.json',
      populate: (dir) => write(dir, 'input.xml', INPUT_XML),
      problem: /meta\.json is missing/,
    },
    {
      name: 'meta.json not an object',
      populate: (dir) => {
        write(dir, 'meta.json', '[]');
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json must be a JSON object/,
    },
    {
      name: 'meta.json missing field',
      populate: (dir) => {
        write(dir, 'meta.json', '{"domain":"tmp","source":"handcrafted","added_by":"tests"}');
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json must have exactly the fields/,
    },
    {
      name: 'meta.json extra field',
      populate: (dir) => {
        write(
          dir,
          'meta.json',
          '{"domain":"tmp","kind":"junit","source":"handcrafted","added_by":"tests","unknown":1}',
        );
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json must have exactly the fields/,
    },
    {
      name: 'meta.json non-string field',
      populate: (dir) => {
        write(
          dir,
          'meta.json',
          '{"domain":"tmp","kind":42,"source":"handcrafted","added_by":"tests"}',
        );
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json field 'kind' must be a non-empty string/,
    },
    {
      name: 'meta.json empty field',
      populate: (dir) => {
        write(dir, 'meta.json', '{"domain":"tmp","kind":"junit","source":"  ","added_by":"tests"}');
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json field 'source' must be a non-empty string/,
    },
    {
      // A present-but-null field is a value refusal, not a key-set refusal —
      // the same classification FixtureLoader.cs applies (AC6).
      name: 'meta.json null field',
      populate: (dir) => {
        write(dir, 'meta.json', '{"domain":"tmp","kind":"junit","source":null,"added_by":"tests"}');
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json field 'source' must be a non-empty string/,
    },
    {
      name: 'meta.json invalid JSON',
      populate: (dir) => {
        write(dir, 'meta.json', 'not json');
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json is not valid JSON/,
    },
    {
      // AC6 parity with FixtureLoaderTests.cs "meta-duplicate-key": .NET refuses
      // this in its Materialize walk; the TS loader scans the raw text because
      // JSON.parse is last-wins before a reviver could see the second key.
      name: 'meta.json duplicate member name',
      populate: (dir) => {
        write(
          dir,
          'meta.json',
          '{"domain":"a","domain":"b","kind":"k","source":"s","added_by":"x"}',
        );
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json is not valid JSON: duplicate member name 'domain'/,
    },
    {
      // An escaped spelling decodes to the same member name — refused exactly as
      // the plain spelling is, matching System.Text.Json decoding.
      name: 'meta.json duplicate member name via escape',
      populate: (dir) => {
        write(
          dir,
          'meta.json',
          '{"domain":"a","dom\\u0061in":"b","kind":"k","source":"s","added_by":"x"}',
        );
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json is not valid JSON: duplicate member name 'domain'/,
    },
    {
      // A duplicate inside a nested value is still a duplicate: refused before
      // the field-shape check, like the .NET Materialize ordering.
      name: 'meta.json duplicate member name nested',
      populate: (dir) => {
        write(
          dir,
          'meta.json',
          '{"domain":"t","kind":"k","source":"s","added_by":"a","x":{"n":1,"n":2}}',
        );
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /meta\.json is not valid JSON: duplicate member name 'n'/,
    },
    {
      // expected.json duplicates stay refused through the canonical-byte check —
      // the raw text cannot equal the canonical form of its collapsed parse.
      name: 'expected.json duplicate member name',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
        write(dir, 'expected.json', '{"a":1,"a":2}');
      },
      problem: /expected\.json must be the RFC 8785 canonical byte sequence/,
    },
    {
      name: 'meta.json UTF-8 BOM',
      populate: (dir) => {
        writeWithBom(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
      },
      problem: /'meta\.json' must be UTF-8 without BOM/,
    },
    {
      name: 'no input file',
      populate: (dir) => write(dir, 'meta.json', VALID_META),
      problem: /an input\.<ext> file is missing/,
    },
    {
      name: 'two input files',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
        write(dir, 'input.txt', 'plain');
      },
      problem: /more than one input\.\* file/,
    },
    {
      name: 'input file without extension',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.', INPUT_XML);
      },
      problem: /the input file has no extension/,
    },
    {
      name: 'stray file',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
        write(dir, 'extra.txt', 'stray');
      },
      problem: /unexpected file 'extra\.txt'/,
    },
    {
      name: 'stray subdirectory',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
        mkdirSync(join(dir, 'nested'));
      },
      problem: /unexpected subdirectory 'nested'/,
    },
    {
      name: 'malformed expected.json',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
        write(dir, 'expected.json', '{');
      },
      problem: /expected\.json is not valid JSON/,
    },
    {
      name: 'expected.json UTF-8 BOM',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        write(dir, 'input.xml', INPUT_XML);
        writeWithBom(dir, 'expected.json', '{}');
      },
      problem: /'expected\.json' must be UTF-8 without BOM/,
    },
    {
      // The payload file gets the same BOM refusal the JSON files get;
      // readFileSync(…, 'utf8') would otherwise keep the U+FEFF.
      name: 'input file UTF-8 BOM',
      populate: (dir) => {
        write(dir, 'meta.json', VALID_META);
        writeWithBom(dir, 'input.xml', INPUT_XML);
      },
      problem: /'input\.xml' must be UTF-8 without BOM/,
    },
  ];

  it.each(refusals)('refuses unknown shape: $name', ({ populate, problem }) => {
    const dir = makeFixtureDir();
    populate(dir);

    let thrown: unknown;
    try {
      loadFixture(dir);
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(FixtureShapeError);
    expect((thrown as FixtureShapeError).fixturePath).toBe(dir);
    expect((thrown as FixtureShapeError).message).toMatch(problem);
  });

  it('tryLoadFixture reports the problem instead of throwing', () => {
    const missing = join(
      tmpdir(),
      `evidgate-missing-${Date.now()}-${Math.random().toString(36).slice(2)}`,
    );

    const refused = tryLoadFixture(missing);
    expect(refused.fixture).toBeNull();
    expect(refused.problem).toMatch(/fixture directory does not exist/);

    const dir = makeFixtureDir();
    write(dir, 'meta.json', VALID_META);
    write(dir, 'input.xml', INPUT_XML);
    const loaded = tryLoadFixture(dir);
    expect(loaded.fixture).not.toBeNull();
    expect(loaded.problem).toBeNull();
  });

  it('refuses an empty or whitespace-only path, matching FixtureLoader.cs', () => {
    // Pinned verbatim: FixtureLoader_EmptyPath_Rejects asserts the same string.
    expect(() => loadFixture('   ')).toThrowError(FixtureShapeError);
    expect(() => loadFixture('   ')).toThrowError('<empty>: fixture directory is empty');

    const refused = tryLoadFixture('   ');
    expect(refused.fixture).toBeNull();
    expect(refused.problem).toBe('<empty>: fixture directory is empty');
  });
});

describe('loadAllFixtures', () => {
  it('finds exactly the sample under the reports domain', () => {
    const fixtures: Fixture[] = loadAllFixtures(join(FIXTURES_ROOT, 'reports'));
    expect(fixtures).toHaveLength(1);
    expect(fixtures[0]!.domain).toBe('reports');
    expect(fixtures[0]!.name).toBe('sample');
  });

  it('finds the sample in the full corpus while skipping non-fixture families', () => {
    const fixtures = loadAllFixtures(FIXTURES_ROOT);
    expect(
      fixtures.some((fixture) => fixture.domain === 'reports' && fixture.name === 'sample'),
    ).toBe(true);
    for (const fixture of fixtures) {
      expect(fixture.meta.domain.length).toBeGreaterThan(0);
      expect(fixture.meta.kind.length).toBeGreaterThan(0);
    }
  });

  it('refuses an empty or whitespace-only root', () => {
    expect(() => loadAllFixtures('')).toThrowError(FixtureShapeError);
    expect(() => loadAllFixtures('')).toThrowError('<empty>: fixtures root is empty');
  });

  it('refuses a missing root', () => {
    expect(() => loadAllFixtures(join(tmpdir(), 'evidgate-no-such-root'))).toThrowError(
      /fixtures root does not exist/,
    );
  });
});
