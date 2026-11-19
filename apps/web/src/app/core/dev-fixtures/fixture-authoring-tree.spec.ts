import { HttpRequest } from '@angular/common/http';
import type { AuthoringTreeResponse } from '@evidgate/sdk';
import { AuthoringTreeFixtureScenario } from './fixture-authoring-tree';
import { FIXTURE_CATALOG_AUTHORING_ROWS } from './fixture-catalog-authoring-rows';
import { matchFixtureRoute } from './fixture-routes';
import { findFixtureEntry } from './fixture-catalog';

const url = '/projects/018f0000-0000-7000-8000-000000000011/authoring/tree';
const rootId = '018f0000-0000-7000-8000-000000000101';
const deepId = '018f0000-0000-7000-8000-000000000102';
const leafId = '018f0000-0000-7000-8000-000000000107';
const request = (query = '') => new HttpRequest('GET', `${url}${query}`);

describe('authoring tree fixture routing', () => {
  it('registers the real SDK operation and its closed response states', () => {
    expect(matchFixtureRoute('GET', url)).toEqual({
      operationId: 'listAuthoringTree',
      method: 'GET',
      path: '/projects/{p}/authoring/tree',
    });
    for (const variant of [
      'ok',
      'loading',
      'empty',
      'error',
      'forbidden',
      'not-found',
      'rate-limited',
    ]) {
      expect(findFixtureEntry('listAuthoringTree', variant)).toBeDefined();
    }
    const entries = FIXTURE_CATALOG_AUTHORING_ROWS;
    expect(entries.length).toBeGreaterThanOrEqual(12);
    expect(new Set(entries.map((entry) => entry.name)).size).toBe(entries.length);
  });

  it('answers addressed and parent reads with root-first ancestors and only their own child page', () => {
    const scenario = new AuthoringTreeFixtureScenario();
    const root = scenario.entry(request(), 'ok')?.document.body as AuthoringTreeResponse;
    expect(root.items.every((node) => node.parent_id === null)).toBe(true);
    const branch = scenario.entry(request(`?parent_id=${rootId}`), 'ok')?.document
      .body as AuthoringTreeResponse;
    expect(branch.node?.node_id).toBe(rootId);
    expect(branch.items.every((node) => node.parent_id === rootId)).toBe(true);
    const deep = scenario.entry(request(`?node_id=${leafId}`), 'ok')?.document
      .body as AuthoringTreeResponse;
    expect(deep.node?.node_id).toBe(leafId);
    expect(deep.path).toHaveLength(6);
    expect(deep.path?.[0]?.node_id).toBe(rootId);
    expect(deep.path?.some((node) => node.title.length > 100)).toBe(true);
    expect(deep.items).toEqual([]);
  });

  it('walks root and child continuations without answering the first page again', () => {
    const scenario = new AuthoringTreeFixtureScenario();
    const roots = scenario.entry(request(), 'ok')?.document.body as AuthoringTreeResponse;
    const next = scenario.entry(request(`?cursor=${roots.next_cursor}`), 'ok')?.document
      .body as AuthoringTreeResponse;
    expect(next.next_cursor).toBeNull();
    expect(
      next.items.some((node) => roots.items.some((first) => first.node_id === node.node_id)),
    ).toBe(false);
    const branch = scenario.entry(request(`?parent_id=${rootId}`), 'ok')?.document
      .body as AuthoringTreeResponse;
    const childPage = scenario.entry(
      request(`?parent_id=${rootId}&cursor=${branch.next_cursor}`),
      'ok',
    )?.document.body as AuthoringTreeResponse;
    expect(childPage.next_cursor).toBeNull();
    expect(childPage.items[0]?.parent_id).toBe(rootId);
    expect(childPage.items[0]?.node_id).not.toBe(branch.items[0]?.node_id);
  });

  it('distinguishes empty and denied and hides unknown node identities', () => {
    const scenario = new AuthoringTreeFixtureScenario();
    expect(scenario.entry(request(), 'empty')?.document.status).toBe(200);
    expect(scenario.entry(request(), 'forbidden')?.document.status).toBe(403);
    expect(
      scenario.entry(request('?node_id=018f0000-0000-7000-8000-000000000199'), 'ok')?.document
        .status,
    ).toBe(404);
    expect(scenario.entry(request(`?parent_id=${rootId}`), 'child-error')?.document.status).toBe(
      503,
    );
    expect(scenario.entry(request(), 'unknown-variant')).toBeUndefined();
  });

  it('answers an empty child branch with the addressed node and path, children only empty', () => {
    const scenario = new AuthoringTreeFixtureScenario();
    const body = scenario.entry(request(`?parent_id=${deepId}`), 'child-empty')?.document
      .body as AuthoringTreeResponse;
    expect(body.node?.node_id).toBe(deepId);
    expect(body.path?.length ?? 0).toBeGreaterThan(0);
    expect(body.path?.some((ancestor) => ancestor.node_id === deepId)).toBe(false);
    expect(body.items).toEqual([]);
    expect(body.next_cursor).toBeNull();
    expect(scenario.entry(request(), 'child-empty')?.document.body).toEqual(
      scenario.entry(request(), 'ok')?.document.body,
    );
  });

  it('makes stale reachable through a successful load followed by a refused refresh', () => {
    const scenario = new AuthoringTreeFixtureScenario();
    expect(scenario.entry(request(), 'stale')?.document.status).toBe(200);
    expect(scenario.entry(request(), 'stale')?.document.status).toBe(503);
    expect(scenario.entry(request(), 'ok')?.document.status).toBe(200);
  });

  it('does not reuse a continuation from a different parent scope', () => {
    const scenario = new AuthoringTreeFixtureScenario();
    expect(scenario.entry(request('?cursor=authoring-child-more'), 'ok')).toBeUndefined();
    expect(
      scenario.entry(request(`?parent_id=${rootId}&cursor=authoring-roots-more`), 'ok'),
    ).toBeUndefined();
    expect(
      scenario.entry(request(`?parent_id=${rootId}&cursor=unknown-cursor`), 'ok'),
    ).toBeUndefined();
  });
});
