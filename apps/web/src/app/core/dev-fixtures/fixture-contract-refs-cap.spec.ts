// The node budget and the eager contract build: `CONTRACT_DOCUMENT` inlines external schema
// references while `fixture-contract.support` is evaluated, so the budget must already be
// initialized at that point. These tests read a synthetic contract and schema through a mocked
// `node:fs`, which is the only way to reach that build with a graph the repository does not hold.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';

/** The synthetic standalone document, swapped per test; also the reference the contract makes. */
const fixture = vi.hoisted(() => ({
  schemaText: '',
  schemaReference: './schemas/cap-fixture.json',
}));

vi.mock('node:fs', async () => {
  const actual = await vi.importActual<Record<string, unknown>>('node:fs');
  const contractPathSuffix = '/contracts/openapi.yaml';
  const schemaPathSuffix = '/contracts/schemas/cap-fixture.json';
  // One operation whose 200 body is the standalone schema, so importing the module under test
  // inlines that reference.
  const contract = JSON.stringify({
    openapi: '3.1.0',
    paths: {
      '/cap': {
        get: {
          operationId: 'readCap',
          responses: {
            '200': {
              description: 'A response whose schema is a standalone document',
              content: { 'application/json': { schema: { $ref: '#/components/schemas/Capped' } } },
            },
          },
        },
      },
    },
    components: {
      schemas: { Capped: { $ref: `${fixture.schemaReference}#/$defs/root` } },
    },
  });
  const existsSync = actual['existsSync'] as (path: string) => boolean;
  const readFileSync = actual['readFileSync'] as (path: string, options?: unknown) => string;

  return {
    ...actual,
    existsSync: (path: string) =>
      path.endsWith(contractPathSuffix) || path.endsWith(schemaPathSuffix) || existsSync(path),
    readFileSync: (path: string, options?: unknown) => {
      if (path.endsWith(contractPathSuffix)) {
        return contract;
      }
      if (path.endsWith(schemaPathSuffix)) {
        return fixture.schemaText;
      }
      return readFileSync(path, options);
    },
  };
});

/** A standalone document whose `$defs/root` references `$defs/leaf` `count` times. */
function repeatedReferences(count: number): unknown {
  return {
    $defs: {
      root: { allOf: Array.from({ length: count }, () => ({ $ref: '#/$defs/leaf' })) },
      leaf: { type: 'string' },
    },
  };
}

/** The component schema the contract keeps when the standalone document cannot be inlined. */
function unexpandedReference(): unknown {
  return { $ref: `${fixture.schemaReference}#/$defs/root` };
}

describe('Fixture contract local reference expansion', () => {
  afterAll(() => {
    // A runner that shares its module registry across files must not keep the synthetic contract.
    vi.doUnmock('node:fs');
    vi.resetModules();
  });

  beforeEach(() => {
    fixture.schemaText = '';
    // Each test rebuilds the module so its eager contract build reads its own schema document.
    vi.resetModules();
  });

  it('expands a bounded local graph and enforces the schema it names', async () => {
    fixture.schemaText = JSON.stringify(repeatedReferences(1));
    const support = await import('./fixture-contract.support');

    expect(support.resolveContractPointer('#/components/schemas/Capped')).toEqual({
      allOf: [{ type: 'string' }],
    });
    expect(support.VALIDATOR.validateResponse('readCap', 200, 'valid').errors).toEqual([]);
    expect(
      support.describeErrors(support.VALIDATOR.validateResponse('readCap', 200, 42).errors),
      'a body the inlined schema types as a string must be refused when it is a number',
    ).toContain('type');
  });

  it('refuses a graph that expands past the node budget while the contract is built', async () => {
    // Far past the budget, so the count does not depend on how the walk is accounted.
    fixture.schemaText = JSON.stringify(repeatedReferences(20_000));
    const support = await import('./fixture-contract.support');

    expect(
      support.resolveContractPointer('#/components/schemas/Capped'),
      'a graph the budget truncated must stay a reference the contract does not own',
    ).toEqual(unexpandedReference());
    expect(support.VALIDATOR.validateResponse('readCap', 200, 'valid').errors).toEqual([
      { index: null, path: '', reason: 'schema_unavailable' },
    ]);
  });

  it('refuses a cyclic local graph instead of rebasing its references onto the contract', async () => {
    fixture.schemaText = JSON.stringify({
      $defs: { root: { $ref: '#/$defs/leaf' }, leaf: { $ref: '#/$defs/root' } },
    });
    const support = await import('./fixture-contract.support');

    expect(
      support.resolveContractPointer('#/components/schemas/Capped'),
      'a cycle must leave a reference behind rather than expand forever',
    ).toEqual(unexpandedReference());
    expect(support.VALIDATOR.validateResponse('readCap', 200, 'valid').errors).toEqual([
      { index: null, path: '', reason: 'schema_unavailable' },
    ]);
  });
});
