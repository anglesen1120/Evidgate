// 09.7.1-T1.1 (plan 09.7.1 §2 AC1, §5 route table; 00 §5.6 W1; docs/design/web/settings.md):
// the route table of the Settings shell, mounted for the `settings` navigation entry only.
//
// The shell is a tab strip and an outlet, so its table has exactly two kinds of child:
//
//   ''         → the landing decision (`settingsIndexGuard`): redirect to the first available tab,
//                or render the "no settings destinations" state when the caller has none;
//   <segment>  → one tab of `SETTINGS_TABS`, gated by `settingsTabGuard`. A tab the caller may not
//                use does not match, the router falls through to the app's `**` route and the
//                not-found pattern answers — never a 403, never a redirect (AC1, 00 §9.2).
//
// Both the strip and this table read `SETTINGS_TABS`, so a tab can never be offered without a route
// or routed without being offered. Delivered tab bodies share the same permission-aware shell;
// the remaining destinations retain their placeholders.
// Other sidebar destinations retain `settings.routes.ts`.
//
// W1 holds: only `app.routes.ts` imports this module, through the per-entry `ENTRY_ROUTES` override.

import type { Type } from '@angular/core';
import type { Route, Routes } from '@angular/router';

import { AuditPage } from './audit/audit.page';
import { MembersPage } from './members/members.page';
import { SecretsPage } from './secrets/secrets.page';
import { PolicyPage } from './policy/policy.page';
import { QuotaPanelComponent } from './quota-retention/quota-panel.component';
import { SettingsNoTabsPage } from './settings-no-tabs.page';
import { SettingsShellPage } from './settings-shell.page';
import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';
import { settingsIndexGuard } from './settings-index.guard';
import { settingsTabGuard } from './settings-tab.guard';
import { SETTINGS_TABS, type SettingsTabId } from './settings-tabs';

/** The tab id -> body component map; tabs without a delivered body mount the placeholder. */
const TAB_BODIES: Readonly<Partial<Record<SettingsTabId, Type<unknown>>>> = {
  members: MembersPage,
  audit: AuditPage,
  secrets: SecretsPage,
  policy: PolicyPage,
  'quota-retention': QuotaPanelComponent,
};

/** One child route per tab, in strip order (the id reaches the body through `data.settingsTabId`). */
const TAB_ROUTES: readonly Route[] = SETTINGS_TABS.map((tab) => ({
  path: tab.segment,
  canMatch: [settingsTabGuard(tab)],
  ...(tab.id === 'api-tokens'
    ? {
        loadComponent: () =>
          import('./api-tokens/api-tokens.page').then((module) => module.ApiTokensPage),
      }
    : { component: TAB_BODIES[tab.id] ?? SettingsTabPlaceholderPage }),
  data: { settingsTabId: tab.id },
}));

export const routes: Routes = [
  {
    path: '',
    component: SettingsShellPage,
    children: [
      // First, so the empty path is the shell's landing route and not a tab's.
      {
        path: '',
        pathMatch: 'full',
        canActivate: [settingsIndexGuard],
        component: SettingsNoTabsPage,
      },
      ...TAB_ROUTES,
    ],
  },
];
