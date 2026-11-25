// The Settings strip: one grouped list of the Settings destinations a caller may open. The Settings
// page renders it, and the sidebar's Settings entry covers every link in it. A destination is either
// a Settings tab (its own page under /orgs/:orgId/settings) or a standalone administration page filed
// under a Settings section. Sections follow SETTINGS_GROUPS; a section with no item is dropped.

import type { SettingsGroupId } from './nav.models';
import { SETTINGS_GROUPS } from './nav-registry';

export interface SettingsStripItem {
  /** The Settings tab id, or the registry id of a standalone administration page. */
  readonly id: string;
  readonly group: SettingsGroupId;
  readonly label: string;
  /** The router commands the item links to. */
  readonly link: readonly string[];
}

export interface SettingsStripSection {
  readonly id: SettingsGroupId;
  readonly label: string;
  readonly items: readonly SettingsStripItem[];
}

/** Buckets the items under their sections, keeping each section's items in the order given. */
export function groupSettingsStrip(
  items: readonly SettingsStripItem[],
): readonly SettingsStripSection[] {
  return SETTINGS_GROUPS.map(({ id, label }) => ({
    id,
    label,
    items: items.filter((item) => item.group === id),
  })).filter((section) => section.items.length > 0);
}
