import type { AuthoringTreeResponse } from '@evidgate/sdk';
import { AUTHORING_TREE_CONTINUATIONS } from './fixture-authoring-tree';
import { FIXTURE_CATALOG_AUTHORING_ROWS } from './fixture-catalog-authoring-rows';
import { VALIDATOR, isRecord } from './fixture-contract.support';

const rows = FIXTURE_CATALOG_AUTHORING_ROWS;

describe('authoring tree fixture contract pins', () => {
  it('validates every hierarchy page and refusal against the current OpenAPI response', () => {
    for (const row of rows) {
      expect(
        VALIDATOR.validateResponse('listAuthoringTree', row.document.status, row.document.body)
          .errors,
        row.name,
      ).toEqual([]);
    }
    expect(rows.filter((row) => row.document.variant === 'ok')).toHaveLength(1);
  });

  it('pins separate root and parent-scoped continuations to disjoint terminal pages', () => {
    for (const continuation of AUTHORING_TREE_CONTINUATIONS) {
      const terminal = rows.find((row) => row.name === continuation.name)?.document
        .body as AuthoringTreeResponse;
      const first = rows
        .map((row) => row.document.body as AuthoringTreeResponse)
        .find((page) => page.next_cursor === continuation.cursor);
      expect(first).toBeDefined();
      expect(terminal.next_cursor).toBeNull();
      expect(terminal.node?.node_id ?? null).toBe(continuation.parentId);
      expect(terminal.items.every((node) => node.parent_id === continuation.parentId)).toBe(true);
      const seen = new Set(first?.items.map((node) => node.node_id));
      expect(terminal.items.some((node) => seen.has(node.node_id))).toBe(false);
    }
  });

  it('refuses invalid UUIDs and error envelopes missing the required retryable member', () => {
    const root = rows.find((row) => row.document.variant === 'ok')?.document
      .body as AuthoringTreeResponse;
    const node = root.items[0];
    if (!node) throw new Error('The roots fixture must contain a node.');
    const invalid = { ...root, items: [{ ...node, node_id: 'invalid-node-uuid' }] };
    expect(
      VALIDATOR.validateResponse('listAuthoringTree', 200, invalid).errors.length,
    ).toBeGreaterThan(0);
    const refusal = structuredClone(
      rows.find((row) => row.document.variant === 'error')?.document.body,
    );
    if (!isRecord(refusal) || !isRecord(refusal['error']))
      throw new Error('A fixture error envelope is required.');
    delete refusal['error']['retryable'];
    expect(
      VALIDATOR.validateResponse('listAuthoringTree', 503, refusal).errors.length,
    ).toBeGreaterThan(0);
  });

  it('keeps count meaning and server path shape explicit without a pending projection', () => {
    for (const row of rows.filter((entry) => entry.document.status === 200)) {
      const page = row.document.body as AuthoringTreeResponse;
      for (const node of page.items) {
        expect(node).toHaveProperty('child_count');
        expect(node).toHaveProperty('item_count');
        expect(node).not.toHaveProperty('pending_change_count');
      }
      if (page.node && page.path) {
        expect(page.path.some((ancestor) => ancestor.node_id === page.node?.node_id)).toBe(false);
      }
    }
  });
});
