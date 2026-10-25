import type { Routes } from '@angular/router';
import { AuditPage } from './audit.page';

/** The `audit` sidebar destination mounts the same page as the Settings Audit tab (09.7.1-T6). */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    component: AuditPage,
    data: { pageHeadingLevel: 1 },
  },
];
