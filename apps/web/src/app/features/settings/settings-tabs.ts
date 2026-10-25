// 09.7.1-T1.1/T1.2 (plan 09.7.1 §5 route table, §2 AC1; plan 09.1.1 T4; D-S68-Q1): the one list of
// Settings tabs. The tab strip renders it, the route table mounts a child route per row, and the
// per-tab `CanMatchFn` decides from it — so the strip and the routes can never disagree about
// which tab exists for a caller.
//
// A tab is available only when BOTH hold:
//
//   1. its product phase has been reached (`isPhaseActive`, the same predicate the sidebar uses): a
//      later-phase tab is hidden and unmatched, never disabled or teased (CR-D-2);
//   2. the server-declared permission set holds its `permission` code (`SessionStore.has`, fed by
//      the replaceable `EffectivePermissionsSource` seam). No role name is ever consulted: a role
//      label confers no privilege on the client (00 §2, D-S68-Q1).
//
// The gate is a convenience, never the authority: the API pipeline (C2) answers every call with its
// own 403/404 (plan 09.7.1 §8 S2).

import type { ProductPhase } from '../../shell/nav/nav.models';
import { isPhaseActive } from '../../shell/nav/product-phase';

/** `members`, `secrets`, ... — also the route segment under `/orgs/:orgId/settings/`. */
export type SettingsTabId =
  | 'members'
  | 'secrets'
  | 'policy'
  | 'quota-retention'
  | 'audit'
  | 'security-events'
  | 'integrations'
  | 'agents';

/** One Settings tab (plan 09.7.1 §5: guard permission per route). */
export interface SettingsTab {
  readonly id: SettingsTabId;
  /** The child-route path under `/orgs/:orgId/settings`; equals `id`. */
  readonly segment: SettingsTabId;
  /** Server-declared `resource:action` code (`contracts/permissions.json`) that gates the tab. */
  readonly permission: string;
  /** CR-D-2 phase in which the tab ships; a later phase than the build's makes it absent. */
  readonly phase: ProductPhase;
  /** Whether the tab's data hangs off `/projects/{p}` (`project`) or `/orgs/{o}` (`org`). */
  readonly scope: 'org' | 'project';
  /** Localised label (`@@settings.tab.<id>`). */
  readonly label: string;
}

/**
 * The tabs in strip order (plan 09.7.1 §10 T1.1). Phases follow the sidebar registry's rows for the
 * same destinations (`nav-registry.ts`): Members, Quota & Retention and Audit are G1; Secrets and
 * Security Events are G2; Policy, Integrations and the V2 Agents tab are G4.
 */
export const SETTINGS_TABS: readonly SettingsTab[] = [
  {
    id: 'members',
    segment: 'members',
    permission: 'membership:read',
    phase: 'G1',
    scope: 'org',
    label: $localize`:@@settings.tab.members:Members`,
  },
  {
    id: 'secrets',
    segment: 'secrets',
    permission: 'ingest_token:read',
    phase: 'G2',
    scope: 'project',
    label: $localize`:@@settings.tab.secrets:Secrets`,
  },
  {
    id: 'policy',
    segment: 'policy',
    permission: 'policy:read',
    phase: 'G4',
    scope: 'project',
    label: $localize`:@@settings.tab.policy:Policy`,
  },
  {
    id: 'quota-retention',
    segment: 'quota-retention',
    permission: 'project:read',
    phase: 'G1',
    scope: 'project',
    label: $localize`:@@settings.tab.quota-retention:Quota & retention`,
  },
  {
    id: 'audit',
    segment: 'audit',
    permission: 'audit:read',
    phase: 'G1',
    scope: 'org',
    label: $localize`:@@settings.tab.audit:Audit`,
  },
  {
    id: 'security-events',
    segment: 'security-events',
    permission: 'security_event:read',
    phase: 'G2',
    scope: 'org',
    label: $localize`:@@settings.tab.security-events:Security events`,
  },
  {
    id: 'integrations',
    segment: 'integrations',
    permission: 'webhook:read',
    phase: 'G4',
    scope: 'project',
    label: $localize`:@@settings.tab.integrations:Integrations`,
  },
  {
    id: 'agents',
    segment: 'agents',
    permission: 'agent_pool:read',
    phase: 'G4',
    scope: 'org',
    label: $localize`:@@settings.tab.agents:Agents`,
  },
];

/**
 * Whether `tab` exists for a caller: its phase is reached and the server declared its permission.
 * Pure, so the guard, the strip and the specs share one decision; `phaseReached` defaults to the
 * build's `isPhaseActive` and is a parameter only so a spec can exercise a later phase.
 */
export function isSettingsTabAvailable(
  tab: SettingsTab,
  has: (code: string) => boolean,
  phaseReached: (phase: ProductPhase) => boolean = isPhaseActive,
): boolean {
  return phaseReached(tab.phase) && has(tab.permission);
}

/** The tabs a caller may see, in strip order. */
export function availableSettingsTabs(
  has: (code: string) => boolean,
  phaseReached: (phase: ProductPhase) => boolean = isPhaseActive,
): readonly SettingsTab[] {
  return SETTINGS_TABS.filter((tab) => isSettingsTabAvailable(tab, has, phaseReached));
}
