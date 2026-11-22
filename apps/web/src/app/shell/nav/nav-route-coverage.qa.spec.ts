import { ORG_SCOPE_ENTRY_ROUTES, PROJECT_SCOPE_ENTRY_ROUTES, routes } from '../../app.routes';
import { NAV_ENTRIES } from './nav-registry';

describe('Navigation registry product route coverage', () => {
  it('mounts both entry collections under their declared organization and project scopes', () => {
    expect(NAV_ENTRIES.length).toBeGreaterThanOrEqual(30);
    const shell = routes.find((route) => route.path === '' && route.loadComponent !== undefined);
    const organization = shell?.children?.find((route) => route.path === 'orgs/:orgId');
    const project = organization?.children?.find((route) => route.path === 'projects/:projectId');
    expect(organization).toBeDefined();
    expect(project).toBeDefined();
    for (const route of ORG_SCOPE_ENTRY_ROUTES) expect(organization?.children).toContain(route);
    for (const route of PROJECT_SCOPE_ENTRY_ROUTES) expect(project?.children).toContain(route);
  });

  it.each(NAV_ENTRIES)(
    'maps $id to one guarded route registration with its exact segment and scope',
    (entry) => {
      const owningRoutes =
        entry.scope === 'org' ? ORG_SCOPE_ENTRY_ROUTES : PROJECT_SCOPE_ENTRY_ROUTES;
      const matches = owningRoutes.filter((route) => route.data?.['entryId'] === entry.id);
      expect(matches).toHaveLength(1);
      const route = matches[0];
      expect(route?.path).toBe(entry.segment);
      expect(route?.canMatch).toHaveLength(2);
      expect(typeof (route?.loadComponent ?? route?.loadChildren)).toBe('function');
      const otherRoutes =
        entry.scope === 'org' ? PROJECT_SCOPE_ENTRY_ROUTES : ORG_SCOPE_ENTRY_ROUTES;
      expect(otherRoutes.some((candidate) => candidate.data?.['entryId'] === entry.id)).toBe(false);
    },
  );
});
