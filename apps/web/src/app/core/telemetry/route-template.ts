// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: route changes are spans, and
// no concrete URL is ever an attribute): the UI route *template* of the active navigation.
//
// `routeTemplate` walks the activated-route tree and joins the `routeConfig.path` strings of
// the matched chain — the literal template the router matched, e.g.
// `/projects/:projectId/runs/:runId`. Parameter *values* live in `snapshot.params`/`url`, and
// those members are never touched: `/projects/p-1/runs/r-1` and `/projects/p-2/runs/r-2`
// report the same template, which is what makes the span attribute a metric dimension rather
// than a customer identifier.
import type { ActivatedRouteSnapshot } from '@angular/router';

/**
 * Reported instead of a template when no route matched (the router has no `routeConfig`).
 * A consumer that sees it knows the value is a placeholder, not a route.
 */
export const UNKNOWN_ROUTE = '/unknown';

/**
 * The matched route template of `root`, segments joined with `/`: the root route alone is
 * `/`, a wildcard match is `/**`, and a chain of componentless parents contributes only the
 * paths it actually has.
 */
export function routeTemplate(root: ActivatedRouteSnapshot): string {
  if (!root.routeConfig) {
    return UNKNOWN_ROUTE;
  }
  const segments: string[] = [];
  for (let node: ActivatedRouteSnapshot | null = root; node !== null; node = node.firstChild) {
    const path = node.routeConfig?.path;
    if (!path) {
      continue;
    }
    for (const segment of path.split('/')) {
      if (segment !== '') {
        segments.push(segment);
      }
    }
  }
  return segments.length === 0 ? '/' : `/${segments.join('/')}`;
}
