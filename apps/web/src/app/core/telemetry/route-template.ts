// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: route changes are spans, and
// no concrete URL is ever an attribute): the UI route *template* of the active navigation.
//
// `routeTemplate` walks the activated-route tree and joins the `routeConfig.path` strings of
// the matched chain — the literal template the router matched, e.g.
// `/projects/:projectId/runs/:runId`. Parameter *values* live in `snapshot.params`/`url`, and
// those members are never touched: `/projects/p-1/runs/r-1` and `/projects/p-2/runs/r-2`
// report the same template, which is what makes the span attribute a metric dimension rather
// than a customer identifier.

/**
 * Reported instead of a template when no route matched: no snapshot of the chain carries a
 * `routeConfig`. A consumer that sees it knows the value is a placeholder, not a route.
 */
export const UNKNOWN_ROUTE = '/unknown';

/**
 * The members `routeTemplate` walks: the matched config, and the next node of the chain. Narrow on
 * purpose, so a caller that has no router state yet (or a spec handing over a synthetic tree) needs
 * no `ActivatedRouteSnapshot`; an `ActivatedRouteSnapshot` is assignable to it.
 */
export interface RouteTreeNode {
  readonly routeConfig: { readonly path?: string } | null;
  readonly firstChild: RouteTreeNode | null;
}

/**
 * The matched route template of the router's root snapshot, segments joined with `/`: a chain
 * that matched only empty paths is `/`, a wildcard match is `/**`, and a componentless parent
 * contributes only the paths it actually has. Angular's own root snapshot never has a
 * `routeConfig` (it is the router state's root, not a configured route), so the walk starts there
 * and only the descendants decide whether anything matched.
 */
export function routeTemplate(root: RouteTreeNode): string {
  const segments: string[] = [];
  let matched = false;
  for (let node: RouteTreeNode | null = root; node !== null; node = node.firstChild) {
    if (!node.routeConfig) {
      continue;
    }
    matched = true;
    for (const segment of (node.routeConfig.path ?? '').split('/')) {
      if (segment !== '') {
        segments.push(segment);
      }
    }
  }
  if (!matched) {
    return UNKNOWN_ROUTE;
  }
  return segments.length === 0 ? '/' : `/${segments.join('/')}`;
}
