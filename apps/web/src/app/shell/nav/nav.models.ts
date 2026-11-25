/** CR-D-2 product phase (docs/design/web-ui/prompts-crd2/00-delta-brief.md): an entry whose phase is later than the active phase is hidden, never disabled or teased. */
export type ProductPhase = 'G1' | 'G2' | 'G3' | 'G4';

/** Sidebar groups in manifest order. */
export type NavGroupId = 'workspace' | 'quality' | 'healing' | 'authoring' | 'administration';

/** Sections of the Settings page; the sidebar shows one Settings entry instead of these. */
export type SettingsGroupId = 'workspace' | 'access' | 'automation' | 'billing';

/** Statically compiled navigation glyphs. */
export type NavIconName =
  | 'grid'
  | 'run'
  | 'inbox'
  | 'shield'
  | 'queue'
  | 'file'
  | 'folder'
  | 'code'
  | 'people'
  | 'play'
  | 'alert'
  | 'wrench'
  | 'book'
  | 'shield-check'
  | 'sliders'
  | 'package'
  | 'rocket'
  | 'chart'
  | 'compass'
  | 'home'
  | 'list'
  | 'map'
  | 'settings'
  | 'users'
  | 'key'
  | 'scroll'
  | 'credit'
  | 'audit'
  | 'shield-alert'
  | 'bot'
  | 'plug'
  | 'life'
  | 'server'
  | 'tree'
  | 'layers'
  | 'sparkle'
  | 'terminal';

/** Where an entry lives in the URL: `/orgs/:orgId/<segment>` or `/orgs/:orgId/projects/:projectId/<segment>`. */
export type NavScope = 'org' | 'project';

/** Contextual pages can name their breadcrumb but cannot be searched without a record. */
export interface NavDetail {
  readonly path: readonly string[];
  readonly label: string;
}

/** One mounted navigation destination. */
export interface NavEntry {
  /** Stable mount id, independent of its localized name. */
  readonly id: string;
  readonly screenId: string;
  /** A separately guarded settings child can show this same screen under the settings shell. */
  readonly settingsTabId?: string;
  readonly parentId?: string;
  readonly details?: readonly NavDetail[];
  readonly group: NavGroupId;
  readonly phase: ProductPhase;
  /** Presentation order inside its group. */
  readonly order: number;
  readonly icon: NavIconName;
  readonly scope: NavScope;
  /** The single URL segment under the scope, for example `pending-changes`. */
  readonly segment: string;
  /** Server-declared `resource:action` code from `contracts/permissions.json` that gates the route and the nav item. */
  readonly permission: string;
  /** The `features/<area>` folder that owns the lazy route. */
  readonly area: string;
  /** Localised label (`$localize` with an `@@shell.nav.<id>` message id). */
  readonly label: string;
  /** Optional visible label; `label` remains the complete accessible name. */
  readonly shortLabel?: string;
  /** Settings section that lists this destination; omitted for the Settings index itself. */
  readonly settingsGroup?: SettingsGroupId;
}

/** A group header with its localised label (`@@shell.nav.group.<id>`). */
export interface NavGroupDef {
  readonly id: NavGroupId;
  readonly label: string;
}

/** What the sidebar renders for one visible entry. `link` is a Router commands array. */
export interface NavItemView {
  readonly parentId?: string;
  readonly details?: readonly NavDetail[];
  readonly id: string;
  readonly label: string;
  readonly shortLabel?: string;
  readonly icon: NavIconName;
  readonly link: readonly string[];
}

/** What the sidebar renders for one group that has at least one visible entry. */
export interface NavGroupView {
  readonly id: NavGroupId;
  readonly label: string;
  readonly items: readonly NavItemView[];
}

/** An entry the build opens although its phase has not been reached, with a leak-gate tag. */
export interface ReviewOpenEntry {
  readonly id: string;
  readonly mount: string;
}

/** A Settings section header with its localised label (`@@shell.settings.group.<id>`). */
export interface SettingsGroupDef {
  readonly id: SettingsGroupId;
  readonly label: string;
}

/** Every visible entry, for breadcrumbs, search and Settings; `groupLabel` names its Settings section. */
export interface NavDestinationView {
  readonly id: string;
  readonly label: string;
  readonly link: readonly string[];
  readonly groupLabel?: string;
}

/** The single Settings entry at the foot of the sidebar; `covers` lists every URL it stands for. */
export interface NavSettingsItem {
  readonly label: string;
  readonly link: readonly string[];
  readonly covers: readonly (readonly string[])[];
}
