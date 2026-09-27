// 01.3.2-T2.4 / AC3 (T42): the fixtures/contracts/ corpus run through Ajv — the
// same files, verdict sidecars and assertions the xUnit suite
// (tests/Contract.Tests/ParityCorpusTests.cs) performs. A vector whose Ajv
// verdict differs from its sidecar fails here, and any vector on which the two
// runtimes disagree fails both suites: the corpus is the contract.
//
// Named parity.test.ts (not parity.spec.ts): 01.6.1-T1 reserves *.spec.ts for
// Playwright E2E and vitest.config.base.mts includes src/**/*.test.ts.
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { ContractSchemaSet, loadContractSchemas } from './validate.js';

interface ExpectedError {
  readonly index: number | null;
  readonly path: string;
  readonly reason: string;
}

interface Verdict {
  readonly schema: string;
  readonly valid: boolean;
  readonly errors?: readonly ExpectedError[];
  readonly comment?: string;
}

const divergenceEnvVar = 'EVIDGATE_PARITY_INCLUDE_DIVERGENCE';
const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const corpusRoot = join(repoRoot, 'fixtures/contracts');
const schemas = loadContractSchemas(join(repoRoot, 'contracts'));

const row = (e: ExpectedError): string => `${e.index ?? '-'}|${e.path}|${e.reason}`;

function* enumerateVectors(includeDivergence: boolean): Generator<{ file: string; rel: string }> {
  for (const directory of readdirSync(join(corpusRoot, 'vectors')).sort()) {
    const dir = join(corpusRoot, 'vectors', directory);
    for (const file of readdirSync(dir).sort()) {
      if (!file.endsWith('.json') || file.endsWith('.verdict.json')) continue;
      yield { file: join(dir, file), rel: `vectors/${directory}/${file}` };
    }
  }

  if (includeDivergence) {
    const root = join(corpusRoot, 'divergence');
    if (existsSync(root)) {
      for (const directory of readdirSync(root).sort()) {
        const dir = join(root, directory);
        for (const file of readdirSync(dir).sort()) {
          if (!file.endsWith('.json') || file.endsWith('.verdict.json')) continue;
          yield { file: join(dir, file), rel: `divergence/${directory}/${file}` };
        }
      }
    }
  }
}

const includeDivergence = process.env[divergenceEnvVar] === '1';
const vectors = [...enumerateVectors(includeDivergence)];

describe('contract parity corpus (fixtures/contracts)', () => {
  it('enumerates at least one vector', () => {
    expect(vectors.length).toBeGreaterThan(0);
  });

  for (const { file, rel } of vectors) {
    it(`verdict: ${rel}`, () => {
      const sidecar = file.slice(0, -'.json'.length) + '.verdict.json';
      expect(existsSync(sidecar), `missing verdict sidecar for ${rel}`).toBe(true);
      const verdict = JSON.parse(readFileSync(sidecar, 'utf8')) as Verdict;

      // The schema key comes from the directory name; a wrong sidecar schema is
      // a corpus authoring error, not a validator verdict.
      const schemaKey = basename(dirname(file));
      expect(verdict.schema, `verdict schema mismatch in ${rel}.verdict.json`).toBe(schemaKey);

      const result = schemas.validate(schemaKey, readFileSync(file));
      const actual = result.errors.map(row).sort();
      const expected = (verdict.errors ?? []).map(row).sort();

      if (verdict.valid) {
        expect(
          result.errors,
          `${rel} expected accept, got ${JSON.stringify(result.errors)}`,
        ).toEqual([]);
      } else {
        expect(
          verdict.errors,
          `${rel}.verdict.json is a reject without a non-empty errors[]`,
        ).toBeTruthy();
        expect(verdict.errors!.length).toBeGreaterThan(0);
        expect(
          actual,
          `${rel}: details[] differ — actual ${JSON.stringify(actual)} vs expected ${JSON.stringify(expected)}`,
        ).toEqual(expected);
      }

      expect(result.errors.length === 0, `${rel}: engine verdict differs from the sidecar`).toBe(
        verdict.valid,
      );
    });
  }

  // The seeded-divergence fixture is the corpus's armed-failure proof (AC3):
  // excluded unless EVIDGATE_PARITY_INCLUDE_DIVERGENCE=1, and armed it must fail
  // — this spec verifies the mechanism by asserting the disagreement itself.
  it('seeded divergence (dv-001) disagrees with its sidecar unless armed', () => {
    const vector = join(
      corpusRoot,
      'divergence',
      'event-envelope.v1',
      'dv-001-seeded-disagreement.json',
    );
    expect(existsSync(vector)).toBe(true);

    if (includeDivergence) {
      // The corpus enumeration above already added dv-001 as a failing case;
      // this assertion documents the armed path only.
      return;
    }

    const verdict = JSON.parse(
      readFileSync(vector.slice(0, -'.json'.length) + '.verdict.json', 'utf8'),
    ) as Verdict;
    const result = schemas.validate(verdict.schema, readFileSync(vector));
    const actual = result.errors.map(row).sort();
    const expected = (verdict.errors ?? []).map(row).sort();
    expect(
      result.errors.length === 0 &&
        actual.length === expected.length &&
        actual.every((a, i) => a === expected[i]),
    ).toBe(false);
  });
});

describe('detail collection cap (fixtures/contracts/README.md)', () => {
  // V-1: the mapped details[] list hard-caps at 100; a further distinct row
  // emits one terminal truncated marker as the last element — the identical
  // rule ContractSchemaSet.Collect applies on the server.
  const eventJson =
    '{"schema_version":1,"event_id":"00000000-0000-0000-0000-000000000001","producer_id":"p","sequence":0,"execution_key":"0000000000000000000000000000000000000000000000000000000000000000","attempt_no":0,"type":"run.log","occurred_at":"2026-01-01T00:00:00Z"}';
  const batch = (count: number): string =>
    `{"events":[${new Array(count).fill(eventJson).join(',')}]}`;

  it('caps at 100 + terminal truncated marker', () => {
    const result = schemas.validate('event-envelope.v1', batch(150));
    expect(result.errors).toHaveLength(101);
    expect(result.errors[100]).toEqual({ index: null, path: '', reason: 'truncated' });
    expect(result.errors.slice(0, 100).every((e) => e.reason === 'minimum')).toBe(true);
  });

  it('exactly 100 rows report verbatim with no marker', () => {
    const result = schemas.validate('event-envelope.v1', batch(100));
    expect(result.errors).toHaveLength(100);
    expect(result.errors.every((e) => e.reason === 'minimum')).toBe(true);
  });
});

describe('self-leaf composite keywords (V-2)', () => {
  // Ad-hoc schemas mirroring the .NET Self_Leaf_Composite_Failures_Map_To_Rows
  // theory — the same triples must come out of Ajv.
  const dialect = 'https://json-schema.org/draft/2020-12/schema';
  const cases: Array<{
    key: string;
    schema: Record<string, unknown>;
    doc: unknown;
    expected: string[];
  }> = [
    {
      key: 'probe-not.v1',
      schema: { not: { const: 1 } },
      doc: 1,
      expected: ['-||not'],
    },
    {
      key: 'probe-not-nested.v1',
      schema: { type: 'object', properties: { a: { not: { const: 1 } } } },
      doc: { a: 1 },
      expected: ['-|/a|not'],
    },
    {
      key: 'probe-not-noise.v1',
      schema: {
        type: 'object',
        properties: { a: { not: { const: 1 } } },
        required: ['x'],
      },
      doc: { a: 2 },
      expected: ['-|/x|required'],
    },
    {
      key: 'probe-anyof.v1',
      schema: { type: 'object', properties: { a: { anyOf: [{ type: 'string' }, { const: 2 }] } } },
      doc: { a: 1 },
      expected: ['-|/a|any_of'],
    },
    {
      key: 'probe-contains.v1',
      schema: { type: 'array', contains: { const: 1 } },
      doc: [],
      expected: ['-||contains'],
    },
    {
      key: 'probe-contains-items.v1',
      schema: { type: 'array', contains: { const: 1 }, items: { type: 'object' } },
      doc: [2],
      expected: ['-|/0|type', '-||contains'],
    },
    {
      key: 'probe-property-names.v1',
      schema: {
        type: 'object',
        properties: { a: { type: 'object', propertyNames: { maxLength: 2 } } },
      },
      doc: { a: { abc: 1 } },
      expected: ['-|/a|max_length'],
    },
    {
      key: 'probe-dependent-schemas.v1',
      schema: { type: 'object', dependentSchemas: { a: { required: ['b'] } } },
      doc: { a: 1 },
      expected: ['-|/b|required'],
    },
    {
      key: 'probe-if-then.v1',
      schema: { type: 'object', if: { required: ['a'] }, then: { required: ['b'] } },
      doc: { a: 1 },
      expected: ['-|/b|required'],
    },
  ];

  let probeSchemas: ContractSchemaSet;
  const root = mkdtempSync(join(tmpdir(), 'contract-probes-'));
  try {
    const dir = join(root, 'contracts', 'schemas');
    mkdirSync(dir, { recursive: true });
    for (const { key, schema } of cases) {
      writeFileSync(join(dir, `${key}.json`), JSON.stringify({ $schema: dialect, ...schema }));
    }
    probeSchemas = loadContractSchemas(join(root, 'contracts'));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }

  for (const { key, doc, expected } of cases) {
    it(`${key} maps the self-leaf composite row`, () => {
      const result = probeSchemas.validateParsed(key, doc);
      expect(result.errors.length === 0).toBe(false);
      expect(result.errors.map(row).sort()).toEqual(expected.slice().sort());
    });
  }
});
