import type { Routes } from '@angular/router';
import { MembersPage } from './members.page';

export const routes: Routes = [{ path: '', pathMatch: 'full', component: MembersPage }];
