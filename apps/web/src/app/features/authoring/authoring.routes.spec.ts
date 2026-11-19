import type { Route, Routes } from '@angular/router';
import { PROJECT_SCOPE_ENTRY_ROUTES } from '../../app.routes';
import { RouteStubPage } from '../../shell/stub/route-stub.page';
import { AuthoringPage } from './authoring.page';
import { routes as areaRoutes } from './authoring.routes';
import { ImportExportPage } from './import-export/import-export.page';
import { PendingChangesPage } from './pending-changes/pending-changes.page';

const TREE_ROUTE: Route = {
  path: '',
  pathMatch: 'full',
  component: AuthoringPage,
  data: { pageHeadingLevel: 1 },
};
const STUB_ROUTE: Routes = [{ path: '', pathMatch: 'full', component: RouteStubPage }];

/** The table's loaders take no arguments; the union also admits a params callback. */
function loadTable(route: Route | undefined): Promise<Routes> {
  const loader = route?.loadChildren;
  if (typeof loader !== 'function') {
    throw new Error(`No route table is mounted on /${route?.path ?? 'missing'}`);
  }
  return (loader as () => Promise<Routes>)();
}

function entryRoute(segment: string): Route | undefined {
  return PROJECT_SCOPE_ENTRY_ROUTES.find((route) => route.path === segment);
}

describe('authoring route tables', () => {
  it('mounts the tree page only on the authoring entry', async () => {
    const table = await loadTable(entryRoute('authoring'));
    expect(table.filter((route) => route.component === AuthoringPage)).toEqual([TREE_ROUTE]);
    expect(table.map((route) => route.path)).toEqual(['', 'import-export']);
    const importExport = table[1];
    if (importExport === undefined) throw new Error('authoring has no import-export child route');
    if (typeof importExport.loadComponent !== 'function')
      throw new Error('the import-export child route has no component loader');
    expect(importExport.data).toEqual({ pageHeadingLevel: 1 });
    expect(await importExport.loadComponent()).toBe(ImportExportPage);
  });

  it('keeps the generation entry on the area placeholder', async () => {
    await expect(loadTable(entryRoute('generation'))).resolves.toEqual(areaRoutes);
    await expect(loadTable(entryRoute('generation'))).resolves.toEqual(STUB_ROUTE);
  });

  it('mounts the pending-changes screen on its own entry route', async () => {
    const table = await loadTable(entryRoute('pending-changes'));
    expect(table.map((route) => route.path)).toEqual(['']);
    const screen = table[0];
    if (screen === undefined || typeof screen.loadComponent !== 'function')
      throw new Error('pending-changes has no component loader');
    expect(screen.data).toEqual({ pageHeadingLevel: 1 });
    expect((await screen.loadComponent()) as unknown).toBe(PendingChangesPage);
  });
});
