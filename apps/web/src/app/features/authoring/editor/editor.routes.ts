import type { CanDeactivateFn, Routes } from '@angular/router';
import { permissionGuard } from '../../../core/guards';
import { EDITOR_READ } from './editor.model';
import type { EditorPage } from './editor.page';

export const editorUnsavedGuard: CanDeactivateFn<EditorPage> = (component) =>
  component.confirmLeave();
export const routes: Routes = [
  {
    path: '',
    canMatch: [permissionGuard(EDITOR_READ)],
    canDeactivate: [editorUnsavedGuard],
    data: { pageHeadingLevel: 1 },
    loadComponent: () => import('./editor.page').then((module) => module.EditorPage),
  },
];
