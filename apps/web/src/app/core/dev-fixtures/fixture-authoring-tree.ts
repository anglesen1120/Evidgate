import type { HttpRequest } from '@angular/common/http';
import type { AuthoringTreeResponse } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog';
import { FIXTURE_CATALOG_AUTHORING_ROWS } from './fixture-catalog-authoring-rows';
import { queryParameter } from './fixture-routes';

const rows = FIXTURE_CATALOG_AUTHORING_ROWS;
const variantEntry = (variant: string) => rows.find((row) => row.document.variant === variant);

// Tree continuations are parent-scoped; the generic single-stream cursor selector is not used.
export const AUTHORING_TREE_CONTINUATIONS = [
  { cursor: 'authoring-roots-more', parentId: null, name: 'authoring-tree-roots-page-2' },
  {
    cursor: 'authoring-child-more',
    parentId: '018f0000-0000-7000-8000-000000000101',
    name: 'authoring-tree-children-page-2',
  },
] as const;

/** The page the corpus holds for one addressed node, ignoring continuation pages. */
function addressedPage(id: string): FixtureEntry | undefined {
  return rows.find((row) => {
    if (
      row.document.status !== 200 ||
      AUTHORING_TREE_CONTINUATIONS.some((page) => page.name === row.name)
    )
      return false;
    return (row.document.body as AuthoringTreeResponse).node?.node_id === id;
  });
}

/** The browser reviews a failed refresh, not a fabricated GET conflict lifecycle. */
export class AuthoringTreeFixtureScenario {
  private readRoots = false;

  entry(request: HttpRequest<unknown>, variant: string): FixtureEntry | undefined {
    const id =
      queryParameter(request.urlWithParams, 'node_id') ??
      queryParameter(request.urlWithParams, 'parent_id');
    const cursor = queryParameter(request.urlWithParams, 'cursor');
    if (variant === 'stale' && !id && !cursor) {
      const result = variantEntry(this.readRoots ? 'error' : 'ok');
      this.readRoots = true;
      return result;
    }
    if (variant === 'child-error') return variantEntry(id ? 'error' : 'ok');
    if (variant === 'child-empty') return this.emptyChildren(id);
    if (!['ok', 'stale', 'loading', 'slow-children'].includes(variant))
      return variantEntry(variant);
    if (cursor) {
      const page = AUTHORING_TREE_CONTINUATIONS.find(
        (entry) => entry.cursor === cursor && entry.parentId === id,
      );
      return rows.find((row) => row.name === page?.name);
    }
    if (!id) return variantEntry(variant === 'loading' ? 'loading' : 'ok');
    const addressed = addressedPage(id);
    if (!addressed) return variantEntry('not-found');
    return variant === 'slow-children'
      ? { ...addressed, document: { ...addressed.document, latency_ms: 3000 } }
      : addressed;
  }

  /** The addressed node and its path, with only the children page empty — never a roots page. */
  private emptyChildren(id: string | null): FixtureEntry | undefined {
    if (!id) return variantEntry('ok');
    const addressed = addressedPage(id);
    const body = addressed?.document.body as AuthoringTreeResponse | undefined;
    if (!addressed || !body) return variantEntry('not-found');
    return {
      ...addressed,
      document: {
        ...addressed.document,
        body: { ...body, items: [], next_cursor: null },
      },
    };
  }
}
