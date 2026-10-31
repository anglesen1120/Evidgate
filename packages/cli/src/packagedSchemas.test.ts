// The package validates from its own build output, never from a repository checkout, so
// the contract schemas are generated into `contracts.generated.ts` by
// `scripts/embed-contracts.mjs`. These specs pin that build product to `contracts/` (any
// drift fails here) and pin the verdicts of the registry built from it.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { EnvelopeInvalidError, createEnvelopeValidator } from './batcher.js';
import { contractSchemaTexts } from './contracts.generated.js';
import { loadPackagedContractSchemas } from './packagedSchemas.js';
import { loadContractSchemas } from './validate.js';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const contractsDirectory = join(repoRoot, 'contracts');

/** The `contracts/` sub-directories the validator loads, recursively. */
const schemaRoots = ['schemas', 'adapter', 'manifest', 'catalog'] as const;

/** The schema a spool line is validated against (the batch envelope). */
const envelopeSchemaKey = 'event-envelope.v1';

/** The smallest plausible contract set: a missing root would silently shrink it. */
const minSchemaCount = 10;

/** `*.json` files of one contract root, keyed by file name without `.json`. */
function contractFilesByKey(): Map<string, string> {
  const files = new Map<string, string>();
  const walk = (directory: string): void => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(path);
      } else if (entry.name.endsWith('.json')) {
        files.set(basename(entry.name, '.json'), path);
      }
    }
  };
  for (const root of schemaRoots) {
    const directory = join(contractsDirectory, root);
    if (existsSync(directory)) {
      walk(directory);
    }
  }
  return files;
}

/** One schema-valid envelope of the 00 §9.4 shape, as the spool stores it. */
function validEnvelope(sequence = 0): string {
  return JSON.stringify({
    schema_version: 1,
    event_id: `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`,
    producer_id: 'pw-shard-0',
    sequence,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  });
}

describe('embedded contract schemas', () => {
  it('carry every schema file of the repository contracts tree', () => {
    const files = contractFilesByKey();
    expect(files.size).toBeGreaterThanOrEqual(minSchemaCount);

    const embedded = Object.keys(contractSchemaTexts).sort();
    expect(embedded).toStrictEqual([...files.keys()].sort());
  });

  it('carry the same JSON text as the contract file they come from', () => {
    const files = contractFilesByKey();
    for (const [key, file] of files) {
      const text = readFileSync(file, 'utf8');
      expect(contractSchemaTexts[key], key).toBe(JSON.stringify(JSON.parse(text)));
    }
  });

  it('compile into the same schema registry as the contracts directory', () => {
    const packaged = loadPackagedContractSchemas();
    const onDisk = loadContractSchemas(contractsDirectory);

    expect([...packaged.keys].sort()).toStrictEqual([...onDisk.keys].sort());
    expect(packaged.keys.length).toBeGreaterThanOrEqual(minSchemaCount);
    expect(packaged.has(envelopeSchemaKey)).toBe(true);
  });
});

describe('envelope validator without a contracts directory', () => {
  it('accepts a schema-valid spool line', () => {
    const validate = createEnvelopeValidator();
    expect(() => {
      validate(validEnvelope(), 0);
    }).not.toThrow();
  });

  it('refuses a spool line with a member the envelope schema does not define', () => {
    const validate = createEnvelopeValidator();
    const line = JSON.stringify({ ...JSON.parse(validEnvelope()), unexpected_member: true });

    expect(() => {
      validate(line, 3);
    }).toThrow(EnvelopeInvalidError);
    try {
      validate(line, 3);
    } catch (error) {
      expect((error as EnvelopeInvalidError).details.map((detail) => detail.reason)).toContain(
        'additional_properties',
      );
    }
  });
});
