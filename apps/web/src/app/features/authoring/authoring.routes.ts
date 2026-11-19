// The `authoring` area's fallback: every entry of this area except `authoring` itself, which
// owns a route table of its own in `authoring-tree.routes.ts`.

import type { Routes } from '@angular/router';

import { RouteStubPage } from '../../shell/stub/route-stub.page';

export const routes: Routes = [{ path: '', pathMatch: 'full', component: RouteStubPage }];
