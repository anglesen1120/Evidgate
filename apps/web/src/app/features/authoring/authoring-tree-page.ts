import type { AuthoringNodePathEntry, NodeDto } from '@evidgate/sdk';
import type { ApiError } from '../../core/http/api-error';
import { retainKeysetRows } from '../../shared/keyset-retention';

export type AuthoringPageStatus = 'idle' | 'loading' | 'ready' | 'error';

/** A capped branch cannot carry a cursor that would continue its retained walk. */
export type AuthoringNodePage = {
  readonly items: readonly NodeDto[];
  readonly error: ApiError | null;
} & (
  | { readonly status: AuthoringPageStatus; readonly nextCursor: string | null }
  | { readonly status: 'capped'; readonly nextCursor: null }
);

export interface AuthoringTreeState {
  readonly projectId: string | null;
  readonly roots: AuthoringNodePage;
  readonly children: Readonly<Record<string, AuthoringNodePage>>;
  readonly selectedNodeId: string | null;
  readonly selectedNode: NodeDto | null;
  readonly path: readonly AuthoringNodePathEntry[];
  readonly selectionStatus: AuthoringPageStatus;
  readonly selectionError: ApiError | null;
  readonly rootsStale: boolean;
  readonly selectionStale: boolean;
}

/** A shared empty branch avoids allocating state for a node nobody has expanded. */
export const EMPTY_NODE_PAGE: AuthoringNodePage = {
  items: [],
  nextCursor: null,
  status: 'idle',
  error: null,
};

export function readyNodePage(
  items: readonly NodeDto[],
  nextCursor: string | null,
  cap: number,
): AuthoringNodePage {
  const retained = retainKeysetRows(items, nextCursor, cap);
  return retained.capped
    ? { items: retained.rows, nextCursor: null, status: 'capped', error: null }
    : { items: retained.rows, nextCursor: retained.nextCursor, status: 'ready', error: null };
}

/** A selection refresh updates known DTOs without throwing away later accepted pages. */
function mergeNodes(loaded: readonly NodeDto[], fresh: readonly NodeDto[]): NodeDto[] {
  const merged = [...loaded];
  const positions = new Map(loaded.map((node, position) => [node.node_id, position]));
  for (const node of fresh) {
    const known = positions.get(node.node_id);
    if (known === undefined) {
      positions.set(node.node_id, merged.length);
      merged.push(node);
    } else {
      merged[known] = node;
    }
  }
  return merged;
}

export function selectionNodePage(
  previous: AuthoringNodePage | undefined,
  items: readonly NodeDto[],
  nextCursor: string | null,
  cap: number,
): AuthoringNodePage {
  if (previous === undefined || previous.items.length === 0)
    return readyNodePage(items, nextCursor, cap);
  const merged = mergeNodes(previous.items, items);
  // A first-page refresh cannot make rows previously discarded by the cap complete again.
  if (previous.status === 'capped') {
    return { ...readyNodePage(merged, null, cap), status: 'capped', nextCursor: null };
  }
  // A longer retained walk owns its cursor; only an exhausted walk adopts the fresh cursor.
  const cursor =
    previous.nextCursor !== null && previous.items.length > items.length
      ? previous.nextCursor
      : nextCursor;
  return readyNodePage(merged, cursor, cap);
}
