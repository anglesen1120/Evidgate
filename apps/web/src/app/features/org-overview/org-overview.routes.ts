// The lazy route table of the `org-overview` area. `app.routes.ts` mounts the dashboard page for
// this entry directly (`ENTRY_PAGES`), so this table is only the area's placeholder. It keeps the
// area folder, its loader and its route module in step with `NAV_ENTRIES`, as `features/project`
// does.

import type { Routes } from '@angular/router';

import { RouteStubPage } from '../../shell/stub/route-stub.page';

/** Everything mounted under an `org-overview` segment that is not the dashboard page. */
export const routes: Routes = [{ path: '', pathMatch: 'full', component: RouteStubPage }];
