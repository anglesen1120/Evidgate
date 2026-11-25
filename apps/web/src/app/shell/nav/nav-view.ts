// The navigation view model: what the sidebar, breadcrumbs, search and the Settings page render,
// in the order they render it. Three filters only:
//
// 1. phase: an entry of a phase not yet reached is absent (hidden, never disabled). Its route
//    guard refuses with the same predicate.
// 2. permission: an entry whose code the session does not hold is absent. The API's own 403
//    remains the real gate; hiding an entry grants nothing.
// 3. scope: a project entry needs a resolved project and an org entry a resolved org, because its
//    link is built from those ids. Before scope resolves the view is empty rather than wrong.
//
// Administration entries are not sidebar groups: the sidebar shows one Settings entry instead, and
// the Settings page lists them by section. A group with no visible item is dropped.

import { computed, inject, Injectable, type Signal } from '@angular/core';

import { SessionStore } from '../../core/session/session.store';
import { availableSettingsTabs } from '../../features/settings/settings-tabs';
import type {
  NavDestinationView,
  NavEntry,
  NavGroupView,
  NavItemView,
  NavSettingsItem,
} from './nav.models';
import { NAV_ENTRIES, NAV_GROUPS, SETTINGS_GROUPS } from './nav-registry';
import { isEntryActive } from './product-phase';
import {
  groupSettingsStrip,
  type SettingsStripItem,
  type SettingsStripSection,
} from './settings-strip';

/** One entry that survived the filters, with the router commands it renders (D-S68-Q4). */
interface VisibleEntry {
  readonly entry: NavEntry;
  readonly link: readonly string[];
}

const ADMINISTRATION_GROUP = 'administration';
const SETTINGS_INDEX_ID = 'settings';

function byOrder(left: VisibleEntry, right: VisibleEntry): number {
  return left.entry.order - right.entry.order;
}

@Injectable({ providedIn: 'root' })
export class NavViewService {
  private readonly session = inject(SessionStore);

  /** Every entry that passes the filters, in registry order, with its link resolved from scope. */
  private readonly visible: Signal<readonly VisibleEntry[]> = computed(() => {
    const org = this.session.currentOrg();
    if (org === null) {
      return [];
    }
    const projectId = this.session.currentProject()?.id ?? null;

    return NAV_ENTRIES.flatMap((entry): VisibleEntry[] => {
      if (!isEntryActive(entry) || !this.session.has(entry.permission)) {
        return [];
      }
      if (entry.scope === 'org') {
        return [{ entry, link: ['/orgs', org.id, entry.segment] }];
      }
      return projectId === null
        ? []
        : [{ entry, link: ['/orgs', org.id, 'projects', projectId, entry.segment] }];
    });
  });

  /**
   * The Settings destinations the caller may open, as one list: the Settings tabs (org-scoped, so
   * they need no project) and the standalone administration pages filed under a section.
   */
  private readonly settingsStrip: Signal<readonly SettingsStripItem[]> = computed(() => {
    const org = this.session.currentOrg();
    if (org === null) {
      return [];
    }
    const tabs = availableSettingsTabs((code) => this.session.has(code)).map(
      (tab): SettingsStripItem => ({
        id: tab.id,
        group: tab.group,
        label: tab.label,
        link: ['/orgs', org.id, 'settings', tab.segment],
      }),
    );
    const pages = this.visible().flatMap(({ entry, link }): SettingsStripItem[] =>
      entry.group === ADMINISTRATION_GROUP && entry.settingsGroup && !entry.settingsTabId
        ? [{ id: entry.id, group: entry.settingsGroup, label: entry.label, link }]
        : [],
    );
    return [...tabs, ...pages];
  });

  /** The Settings strip bucketed under its sections, in journey order. */
  readonly settingsSections: Signal<readonly SettingsStripSection[]> = computed(() =>
    groupSettingsStrip(this.settingsStrip()),
  );

  /**
   * The groups the navigation renders for the current session and scope, groups in journey order and
   * items in registry order. Empty until an org is resolved — the shell shows the workspace
   * switcher and the landing guard picks the destination. Breadcrumbs and search read this too,
   * so administration entries stay here even though the sidebar hides their group.
   */
  readonly groups: Signal<readonly NavGroupView[]> = computed(() => {
    const visible = this.visible();
    const groups: NavGroupView[] = [];
    for (const group of NAV_GROUPS) {
      const items: NavItemView[] = visible
        .filter(({ entry }) => entry.group === group.id)
        .sort(byOrder)
        .map(({ entry, link }) => ({
          id: entry.id,
          ...(entry.parentId ? { parentId: entry.parentId } : {}),
          ...(entry.details ? { details: entry.details } : {}),
          label: entry.label,
          shortLabel: entry.shortLabel ?? entry.label,
          icon: entry.icon,
          link,
        }));
      if (items.length > 0) {
        groups.push({ id: group.id, label: group.label, items });
      }
    }
    return groups;
  });

  /** The sidebar's groups: `groups` without administration, which the single Settings entry covers. */
  readonly sidebarGroups: Signal<readonly NavGroupView[]> = computed(() =>
    this.groups().filter((group) => group.id !== ADMINISTRATION_GROUP),
  );

  /** Every visible destination in registry order, the shared contract for palette and search. */
  readonly destinations: Signal<readonly NavDestinationView[]> = computed(() =>
    this.visible().map(({ entry, link }) => ({
      id: entry.id,
      label: entry.label,
      link,
      ...(entry.settingsGroup ? { groupLabel: this.settingsGroupLabel(entry.settingsGroup) } : {}),
    })),
  );

  /**
   * The single Settings entry at the foot of the sidebar; null when no administration destination is
   * visible. It lands on the Settings index when that index has a tab the caller may open, else on
   * the first standalone administration page the caller holds. `covers` lists every URL it stands
   * for, so it reads as active on any of them.
   */
  readonly settings: Signal<NavSettingsItem | null> = computed(() => {
    const administration = this.visible().filter(
      ({ entry }) => entry.group === ADMINISTRATION_GROUP,
    );
    const index = administration.find(({ entry }) => entry.id === SETTINGS_INDEX_ID);
    const standalone = administration.find(({ entry }) => entry.id !== SETTINGS_INDEX_ID);
    const tabOpen = availableSettingsTabs((code) => this.session.has(code)).length > 0;
    const landing = index !== undefined && tabOpen ? index : (standalone ?? index);
    const label = NAV_ENTRIES.find((entry) => entry.id === SETTINGS_INDEX_ID)?.label;
    if (landing === undefined || label === undefined) {
      return null;
    }
    return {
      label,
      link: landing.link,
      covers: [
        ...administration.map(({ link }) => link),
        ...this.settingsStrip().map(({ link }) => link),
      ],
    };
  });

  private settingsGroupLabel(id: NonNullable<NavEntry['settingsGroup']>): string {
    return SETTINGS_GROUPS.find((group) => group.id === id)?.label ?? id;
  }
}
