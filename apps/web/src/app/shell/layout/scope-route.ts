import { PRIMARY_OUTLET, Router, UrlSegment, UrlTree } from '@angular/router';

/**
 * The URL that switches the project inside one organization. The entry screen, its query string and
 * its fragment stay; only the project id is replaced. Deeper segments belong to the previous
 * project's data (a run id, a node id), so they are dropped instead of carried over.
 */
export function projectSwitchTree(router: Router, orgId: string, projectId: string): UrlTree {
  const tree = router.parseUrl(router.url);
  const primary = tree.root.children[PRIMARY_OUTLET];
  const projectIndex = primary?.segments.findIndex((segment) => segment.path === 'projects') ?? -1;
  const previous = primary?.segments[projectIndex + 1];
  if (primary && projectIndex >= 0 && previous) {
    const entry = primary.segments[projectIndex + 2] ?? new UrlSegment('overview', {});
    primary.segments.splice(
      projectIndex + 1,
      primary.segments.length - projectIndex - 1,
      new UrlSegment(projectId, previous.parameters),
      entry,
    );
    return tree;
  }
  return router.createUrlTree(['/orgs', orgId, 'projects', projectId, 'overview'], {
    queryParams: tree.queryParams,
    ...(tree.fragment === null ? {} : { fragment: tree.fragment }),
  });
}
