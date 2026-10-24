// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: route changes are spans, and no
// concrete URL is ever an attribute): the route template is read off the matched
// `routeConfig.path` chain only. The fixtures below are activated-route trees with just the two
// members the function reads — `routeConfig.path` and `firstChild` — so a test can also hand it
// the parameter values and URL segments of a real navigation and prove they are ignored.
import type { ActivatedRouteSnapshot } from '@angular/router';

import { routeTemplate, UNKNOWN_ROUTE } from './route-template';

/**
 * One snapshot of the chain, outermost first: the path array becomes the activated-route tree.
 * `null` is a snapshot the router could not match, i.e. no `routeConfig` at all.
 */
function snapshot(paths: readonly (string | null)[]): ActivatedRouteSnapshot {
  const [path, ...rest] = paths;
  if (path === undefined) {
    throw new Error('snapshot needs at least one route');
  }
  const node = {
    routeConfig: path === null ? null : { path },
    firstChild: rest.length === 0 ? null : snapshot(rest),
  };
  return node as unknown as ActivatedRouteSnapshot;
}

describe('routeTemplate', () => {
  it('reports the root route alone as a slash', () => {
    expect(routeTemplate(snapshot(['']))).toBe('/');
  });

  it('joins the paths of the matched chain', () => {
    expect(routeTemplate(snapshot(['', 'projects/:projectId', 'runs/:runId']))).toBe(
      '/projects/:projectId/runs/:runId',
    );
  });

  it('keeps a wildcard match as a wildcard', () => {
    expect(routeTemplate(snapshot(['', '**']))).toBe('/**');
    expect(routeTemplate(snapshot(['', 'projects/:projectId', '**']))).toBe(
      '/projects/:projectId/**',
    );
  });

  it('skips a componentless parent without a path', () => {
    expect(routeTemplate(snapshot(['', '', 'projects/:projectId']))).toBe('/projects/:projectId');
  });

  it('reports a pathful root as it is', () => {
    expect(routeTemplate(snapshot(['projects/:projectId']))).toBe('/projects/:projectId');
  });

  it('reports the unknown-route placeholder when no route matched', () => {
    expect(routeTemplate(snapshot([null]))).toBe(UNKNOWN_ROUTE);
    expect(UNKNOWN_ROUTE).toBe('/unknown');
  });

  it('walks from the configless root snapshot Angular always hands over', () => {
    // `router.routerState.snapshot.root` is the router state's own root: `routeConfig === null`,
    // the configured routes are its descendants. The app's route table starts with a pathless
    // layout route, then `orgs/:orgId`, then `projects/:projectId`, then the page.
    expect(routeTemplate(snapshot([null, '', 'orgs/:orgId', 'projects/:projectId', 'runs']))).toBe(
      '/orgs/:orgId/projects/:projectId/runs',
    );
    expect(routeTemplate(snapshot([null, 'login']))).toBe('/login');
    expect(routeTemplate(snapshot([null, '', '**']))).toBe('/**');
  });

  it('reports a configless root with only pathless descendants as a slash', () => {
    expect(routeTemplate(snapshot([null, '']))).toBe('/');
  });

  it('ignores parameter values and URL segments of the real navigation', () => {
    const root = {
      routeConfig: { path: '' },
      firstChild: {
        routeConfig: { path: 'projects/:projectId' },
        params: { projectId: 'p-1' },
        url: [{ path: 'p-1' }],
        firstChild: null,
      },
    } as unknown as ActivatedRouteSnapshot;

    expect(routeTemplate(root)).toBe('/projects/:projectId');
  });

  it('normalises a path that carries slashes of its own', () => {
    expect(routeTemplate(snapshot(['', 'projects/:projectId/', '/runs']))).toBe(
      '/projects/:projectId/runs',
    );
  });
});
