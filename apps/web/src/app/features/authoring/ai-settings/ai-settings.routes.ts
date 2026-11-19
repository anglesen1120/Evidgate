import type { Routes } from '@angular/router';
import { permissionGuard } from '../../../core/guards';
import { AI_READ } from './ai-settings.model';
export const routes: Routes = [
  {
    path: '',
    canMatch: [permissionGuard(AI_READ)],
    data: { pageHeadingLevel: 1 },
    loadComponent: () => import('./ai-settings.page').then((m) => m.AiSettingsPage),
  },
];
