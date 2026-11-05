// 04.7.1-T1.2/T1.4 and 05.1.1-T1.1/T1.4: the loader contract the CLI shares with
// the server (Evidgate.BuildingBlocks.Contracts.Validation.ContractSchemaSet,
// mirrored by tests/Contract.Tests/ContractSchemaRegistryTests.cs) — recursive
// discovery of contracts/{schemas,adapter/**,manifest}, keys that are never
// silently overwritten, cross-file $refs resolved from the loaded set only, and
// the x-max-bytes cap. The per-vector verdicts of the documents that cross a file
// boundary live in fixtures/contracts/vectors/execute.v1|collect.v1 (parity.test.ts).
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { loadContractSchemas } from './validate.js';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const dialect = 'https://json-schema.org/draft/2020-12/schema';

/** The 00 §9.4 event the execute/collect operations carry, referenced by $id. */
const validEvent = {
  schema_version: 1,
  event_id: '00000000-0000-0000-0000-000000000005',
  producer_id: 'playwright-shard-1',
  sequence: 12,
  execution_key: '0'.repeat(64),
  attempt_no: 1,
  type: 'test.step',
  occurred_at: '2026-01-01T00:00:00Z',
};

const executeDocument = (events: unknown[]) => ({
  protocol: { major: 1, minor: 0 },
  operation: 'execute',
  events,
});

/** A throwaway `contracts/` tree whose directory name is random (the schema key can be too). */
function withTree(files: Record<string, unknown>, run: (root: string) => void): void {
  const root = join(mkdtempSync(join(tmpdir(), 'contract-tree-')), 'contracts');
  try {
    for (const [relative, document] of Object.entries(files)) {
      const path = join(root, relative);
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, JSON.stringify(document));
    }
    run(root);
  } finally {
    rmSync(dirname(root), { recursive: true, force: true });
  }
}

describe('contract schema registry', () => {
  it('discovers nested adapter operations, the config document and the manifest', () => {
    const schemas = loadContractSchemas(join(repoRoot, 'contracts'));

    for (const key of [
      'event-envelope.v1',
      'capability.v1',
      'config.v1',
      'discover.v1',
      'prepare.v1',
      'execute.v1',
      'collect.v1',
      'cleanup.v1',
      'manifest.v1',
    ]) {
      expect(schemas.has(key), `${key} must load from contracts/**`).toBe(true);
    }

    // The transport caps the plans decide: 04.7.1 §8 (16 KiB) and 04.1.3-T1 (32 MiB manifest body).
    expect(schemas.maxBytes('capability.v1')).toBe(16384);
    expect(schemas.maxBytes('manifest.v1')).toBe(33554432);
  });

  it('resolves the operation envelope $ref from the loaded set, not the file system', () => {
    const schemas = loadContractSchemas(join(repoRoot, 'contracts'));

    expect(schemas.validateParsed('execute.v1', executeDocument([validEvent])).errors).toEqual([]);

    // A removed envelope member and an envelope conditional both bite through
    // the cross-file $ref, so the reference is live in this runtime too.
    const missingMember: Record<string, unknown> = { ...validEvent };
    delete missingMember.event_id;
    expect(schemas.validateParsed('execute.v1', executeDocument([missingMember])).errors).toEqual([
      { index: null, path: '/events/0/event_id', reason: 'required' },
    ]);

    const finished = { ...validEvent, type: 'test.finished' };
    expect(schemas.validateParsed('execute.v1', executeDocument([finished])).errors).toEqual([
      { index: null, path: '/events/0/status', reason: 'required' },
    ]);
  });

  it('fails loud on a duplicate key instead of last-one-wins', () => {
    const key = `dup-${Math.random().toString(36).slice(2)}.v1`;
    const minimal = { $schema: dialect, $id: `${key}.json`, type: 'object' };

    withTree(
      { [`schemas/${key}.json`]: minimal, [`adapter/operations/${key}.json`]: minimal },
      (root) => {
        expect(() => loadContractSchemas(root)).toThrow(
          new RegExp(`duplicate contract schema key '${key.replace('.', '\\.')}'`),
        );
      },
    );
  });

  it('fails loud on a $ref outside the loaded set', () => {
    const absent = `absent-${Math.random().toString(36).slice(2)}.v1`;
    const key = `dangling-${Math.random().toString(36).slice(2)}.v1`;

    withTree(
      {
        [`schemas/${key}.json`]: {
          $schema: dialect,
          $id: `${key}.json`,
          $ref: `urn:evidgate:contract:${absent}`,
        },
      },
      (root) => {
        expect(() => loadContractSchemas(root)).toThrow(new RegExp(absent.replace('.', '\\.')));
      },
    );
  });

  it('rejects a body over x-max-bytes before parsing', () => {
    const schemas = loadContractSchemas(join(repoRoot, 'contracts'));
    const overCap = new Uint8Array(schemas.maxBytes('manifest.v1')! + 1);

    expect(schemas.validate('manifest.v1', overCap).errors).toEqual([
      { index: null, path: '', reason: 'body_too_large' },
    ]);
  });

  it('encodes the manifest bounds in the schema', () => {
    const schemas = loadContractSchemas(join(repoRoot, 'contracts'));
    const atBounds = (plannedExecutions: number, shards: number) => ({
      schema_version: 1,
      suite_revision_id: '00000000-0000-0000-0000-000000000020',
      planned_executions: Array.from({ length: plannedExecutions }, () => ({
        case_revision_id: '00000000-0000-0000-0000-000000000010',
        variant: { params: {} },
        target_env: 'e',
      })),
      matrix: { case_revision_ids: [], variants: [], target_envs: [] },
      shard_plan: {
        shards: Array.from({ length: shards }, () => ({
          shard_id: 's',
          from_index: 0,
          to_index: 0,
        })),
      },
      env_needs: { target_envs: [] },
    });

    // 10 000 planned executions and 256 shards are the declared bounds (05.1.1-T1.2).
    expect(schemas.validateParsed('manifest.v1', atBounds(10000, 256)).errors).toEqual([]);
    expect(schemas.validateParsed('manifest.v1', atBounds(10001, 1)).errors).toEqual([
      { index: null, path: '/planned_executions', reason: 'max_items' },
    ]);
    expect(schemas.validateParsed('manifest.v1', atBounds(1, 257)).errors).toEqual([
      { index: null, path: '/shard_plan/shards', reason: 'max_items' },
    ]);
  });
});
