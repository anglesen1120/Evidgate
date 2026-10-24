// 09.1.1-T4.1 (plan 09.1.1 §10 T4; docs/design/web/layout.md): the types the navigation registry
// (`nav-registry.ts`), the nav view builder (`nav-view.ts`), the route table and the shell layout
// components share. They are application view types, not wire DTOs: nothing here mirrors an API
// payload.

/** CR-D-2 product phase (docs/design/web-ui/prompts-crd2/00-delta-brief.md): an entry whose phase is later than the active phase is hidden, never disabled or teased. */
export type ProductPhase = 'G1' | 'G2' | 'G3' | 'G4';

/** Sidebar groups in journey order (v2 `GROUP_ORDER` D, R, L, X, S). */
export type NavGroupId = 'design' | 'runs' | 'learn' | 'release' | 'settings';

/** Icon names the sidebar uses (the v2 sprite names of every `sidebar: true` route). */
export type NavIconName =
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

/** One registry row (a v2 `ROUTES` entry with `sidebar: true`). */
export interface NavEntry {
  /** v2 route id, for example `authoring`. Unique. */
  readonly id: string;
  readonly group: NavGroupId;
  readonly phase: ProductPhase;
  /** Sort key inside the group (v2 `order`). */
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
}

/** A group header with its localised label (`@@shell.nav.group.<id>`). */
export interface NavGroupDef {
  readonly id: NavGroupId;
  readonly label: string;
}

/** What the sidebar renders for one visible entry. `link` is a Router commands array. */
export interface NavItemView {
  readonly id: string;
  readonly label: string;
  readonly icon: NavIconName;
  readonly link: readonly string[];
}

/** What the sidebar renders for one group that has at least one visible entry. */
export interface NavGroupView {
  readonly id: NavGroupId;
  readonly label: string;
  readonly items: readonly NavItemView[];
}
