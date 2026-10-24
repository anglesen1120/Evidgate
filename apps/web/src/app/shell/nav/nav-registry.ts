// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1, §5; CR-D-2 navigation rule; docs/design/web/layout.md): the
// navigation registry — one row per sidebar entry of the owner-selected v2 mockup
// (`docs/design/web-ui/output/assets/evidgate.js`, `ROUTES` with `sidebar: true`). It is the only
// hand-written navigation list in the app: the route table mounts an entry by this row, the sidebar
// renders it from this row, and the placeholder page names itself from this row.
//
// Shape of a row:
//
// - `phase` is the v2 phase, filtered by `ACTIVE_PHASE` (`product-phase.ts`);
// - `permission` is a `resource:action` code that exists in `contracts/permissions.json` (the
//   single source of permission strings, 00 §2). It is chosen from the feature's own endpoint in
//   `contracts/openapi.yaml` (`x-permission`, T51) where that endpoint exists, and from the
//   closest real code of the same resource where it does not — no new code is invented here:
//
//   | entry | code | endpoint family that decides it |
//   |---|---|---|
//   | authoring / overview / ai-settings | `authoring:read`, `project:read` | `/projects/{p}/authoring/*`, `GET /projects/{p}` |
//   | generation | `authoring:generate` | generation endpoints (13.3.1) |
//   | pending-changes | `authoring:write` | authoring write endpoints (batched review) |
//   | onboarding | `project:create` | `POST /orgs/{o}/projects` |
//   | scripts, runs, quality, releases | `run:read` | `GET /projects/{p}/runs`, `GET /releases/{id}` |
//   | catalog, mapping | `case:read` | `GET /projects/{p}/cases` |
//   | failures, repairs, knowledge, availability | `healing:triage` | `GET /projects/{p}/incidents`, `GET /healing/switch` |
//   | studio | `draft:write` | draft endpoints (`draft:*`, V2) |
//   | suites | `suite:read` | `GET /projects/{p}/suites` |
//   | policies | `policy:read` | `GET /policies/{id}`, `POST /projects/{p}/policies` |
//   | deploy | `environment:write` | `POST /projects/{p}/environments/{e}/revisions` |
//   | settings / members / quota / audit / security / support | `organization:read`, `membership:read`, `usage:read`, `audit:read`, `security_event:read`, `support:read` | `GET /orgs/{o}`, `/memberships`, `/usage/*`, `/audit`, `/security-events`, `/support-grants` |
//   | secrets / integrations / tokens / agents | `ingest_token:read`, `webhook:read`, `service_identity:read`, `agent_pool:read` | `/projects/{p}/ingest-tokens`, `/projects/{p}/webhook-endpoints`, `/orgs/{o}/service-identities`, agent pools (05.7.3) |
//
// - `scope` follows the API path, not the sidebar group: an entry whose endpoints hang off
//   `/orgs/{o}` is `org`, one whose endpoints hang off `/projects/{p}` is `project`. That is why
//   several Settings entries are project-scope (`secrets`, `integrations`, `ai-settings`) while
//   their siblings are org-scope;
// - `segment` is the URL segment under `/orgs/:orgId/` (or `/orgs/:orgId/projects/:projectId/`),
//   the kebab id except `quota`, whose URL segment is `usage` (v2 `31-quota-retention`);
// - `area` is the `features/<area>` folder that owns the lazy route; entries of one journey step
//   share a folder (`authoring` holds the tree, generation and pending changes; `project` holds the
//   overview and onboarding; `settings` holds the whole settings group), so one chunk serves them.
//
// Labels carry explicit message ids (`@@shell.nav.<id>`, `@@shell.nav.group.<id>`) because the `vi`
// build is keyed on the catalog (09.1.1-T6.1).

import type { NavEntry, NavGroupDef } from './nav.models';

/** The five sidebar groups in journey order (v2 `GROUP_ORDER` D, R, L, X, S). */
export const NAV_GROUPS: readonly NavGroupDef[] = [
  { id: 'design', label: $localize`:@@shell.nav.group.design:Test design` },
  { id: 'runs', label: $localize`:@@shell.nav.group.runs:Scripts & runs` },
  { id: 'learn', label: $localize`:@@shell.nav.group.learn:Learn & repair` },
  { id: 'release', label: $localize`:@@shell.nav.group.release:Release` },
  { id: 'settings', label: $localize`:@@shell.nav.group.settings:Settings` },
];

/**
 * Every sidebar entry of the v2 mockup, in group-then-order sequence. Ids and segments are unique;
 * `nav-registry.spec.ts` pins that and that every `permission` is a code of
 * `contracts/permissions.json`.
 */
export const NAV_ENTRIES: readonly NavEntry[] = [
  // D — Test design (G1)
  {
    id: 'authoring',
    group: 'design',
    phase: 'G1',
    order: 1,
    icon: 'tree',
    scope: 'project',
    segment: 'authoring',
    permission: 'authoring:read',
    area: 'authoring',
    label: $localize`:@@shell.nav.authoring:Feature tree`,
  },
  {
    id: 'generation',
    group: 'design',
    phase: 'G1',
    order: 2,
    icon: 'sparkle',
    scope: 'project',
    segment: 'generation',
    permission: 'authoring:generate',
    area: 'authoring',
    label: $localize`:@@shell.nav.generation:Generate tests`,
  },
  {
    id: 'pending-changes',
    group: 'design',
    phase: 'G1',
    order: 3,
    icon: 'layers',
    scope: 'project',
    segment: 'pending-changes',
    permission: 'authoring:write',
    area: 'authoring',
    label: $localize`:@@shell.nav.pending-changes:Pending changes`,
  },
  {
    id: 'overview',
    group: 'design',
    phase: 'G1',
    order: 4,
    icon: 'home',
    scope: 'project',
    segment: 'overview',
    permission: 'project:read',
    area: 'project',
    label: $localize`:@@shell.nav.overview:Overview`,
  },
  {
    id: 'onboarding',
    group: 'design',
    phase: 'G1',
    order: 5,
    icon: 'compass',
    scope: 'org',
    segment: 'onboarding',
    permission: 'project:create',
    area: 'project',
    label: $localize`:@@shell.nav.onboarding:Onboarding`,
  },

  // R — Scripts & runs (G2)
  {
    id: 'scripts',
    group: 'runs',
    phase: 'G2',
    order: 1,
    icon: 'terminal',
    scope: 'project',
    segment: 'scripts',
    permission: 'run:read',
    area: 'scripts',
    label: $localize`:@@shell.nav.scripts:Generated scripts`,
  },
  {
    id: 'catalog',
    group: 'runs',
    phase: 'G2',
    order: 2,
    icon: 'list',
    scope: 'project',
    segment: 'catalog',
    permission: 'case:read',
    area: 'catalog',
    label: $localize`:@@shell.nav.catalog:Test catalog`,
  },
  {
    id: 'mapping',
    group: 'runs',
    phase: 'G2',
    order: 3,
    icon: 'map',
    scope: 'project',
    segment: 'mapping',
    permission: 'case:read',
    area: 'mapping',
    label: $localize`:@@shell.nav.mapping:Mapping resolution`,
  },
  {
    id: 'runs',
    group: 'runs',
    phase: 'G2',
    order: 4,
    icon: 'play',
    scope: 'project',
    segment: 'runs',
    permission: 'run:read',
    area: 'runs',
    label: $localize`:@@shell.nav.runs:Runs`,
  },

  // L — Learn & repair (G3)
  {
    id: 'failures',
    group: 'learn',
    phase: 'G3',
    order: 1,
    icon: 'alert',
    scope: 'project',
    segment: 'failures',
    permission: 'healing:triage',
    area: 'failures',
    label: $localize`:@@shell.nav.failures:Failure Inbox`,
  },
  {
    id: 'repairs',
    group: 'learn',
    phase: 'G3',
    order: 2,
    icon: 'wrench',
    scope: 'project',
    segment: 'repairs',
    permission: 'healing:triage',
    area: 'repairs',
    label: $localize`:@@shell.nav.repairs:Repair proposals`,
  },
  {
    id: 'knowledge',
    group: 'learn',
    phase: 'G3',
    order: 3,
    icon: 'book',
    scope: 'project',
    segment: 'knowledge',
    permission: 'healing:triage',
    area: 'knowledge',
    label: $localize`:@@shell.nav.knowledge:Knowledge lineage`,
  },
  {
    id: 'availability',
    group: 'learn',
    phase: 'G3',
    order: 4,
    icon: 'shield-check',
    scope: 'project',
    segment: 'availability',
    permission: 'healing:triage',
    area: 'availability',
    label: $localize`:@@shell.nav.availability:Availability`,
  },

  // X — Release (G4)
  {
    id: 'studio',
    group: 'release',
    phase: 'G4',
    order: 1,
    icon: 'sliders',
    scope: 'project',
    segment: 'studio',
    permission: 'draft:write',
    area: 'studio',
    label: $localize`:@@shell.nav.studio:Test Studio`,
  },
  {
    id: 'suites',
    group: 'release',
    phase: 'G4',
    order: 2,
    icon: 'package',
    scope: 'project',
    segment: 'suites',
    permission: 'suite:read',
    area: 'suites',
    label: $localize`:@@shell.nav.suites:Suite composition`,
  },
  {
    id: 'quality',
    group: 'release',
    phase: 'G4',
    order: 3,
    icon: 'chart',
    scope: 'project',
    segment: 'quality',
    permission: 'run:read',
    area: 'quality',
    label: $localize`:@@shell.nav.quality:Quality`,
  },
  {
    id: 'releases',
    group: 'release',
    phase: 'G4',
    order: 4,
    icon: 'rocket',
    scope: 'project',
    segment: 'releases',
    permission: 'run:read',
    area: 'releases',
    label: $localize`:@@shell.nav.releases:Releases`,
  },
  {
    id: 'policies',
    group: 'release',
    phase: 'G4',
    order: 5,
    icon: 'scroll',
    scope: 'project',
    segment: 'policies',
    permission: 'policy:read',
    area: 'policies',
    label: $localize`:@@shell.nav.policies:Release policies`,
  },
  {
    id: 'deploy',
    group: 'release',
    phase: 'G4',
    order: 6,
    icon: 'server',
    scope: 'project',
    segment: 'deploy',
    permission: 'environment:write',
    area: 'deploy',
    label: $localize`:@@shell.nav.deploy:Deploy targets`,
  },

  // S — Settings (G1/G2/G4)
  {
    id: 'settings',
    group: 'settings',
    phase: 'G1',
    order: 1,
    icon: 'settings',
    scope: 'org',
    segment: 'settings',
    permission: 'organization:read',
    area: 'settings',
    label: $localize`:@@shell.nav.settings:General`,
  },
  {
    id: 'ai-settings',
    group: 'settings',
    phase: 'G1',
    order: 2,
    icon: 'sparkle',
    scope: 'project',
    segment: 'ai-settings',
    permission: 'project:read',
    area: 'settings',
    label: $localize`:@@shell.nav.ai-settings:AI settings`,
  },
  {
    id: 'members',
    group: 'settings',
    phase: 'G1',
    order: 3,
    icon: 'users',
    scope: 'org',
    segment: 'members',
    permission: 'membership:read',
    area: 'settings',
    label: $localize`:@@shell.nav.members:Members & roles`,
  },
  {
    id: 'quota',
    group: 'settings',
    phase: 'G1',
    order: 4,
    icon: 'credit',
    scope: 'org',
    segment: 'usage',
    permission: 'usage:read',
    area: 'settings',
    label: $localize`:@@shell.nav.quota:Usage & credits`,
  },
  {
    id: 'audit',
    group: 'settings',
    phase: 'G1',
    order: 5,
    icon: 'audit',
    scope: 'org',
    segment: 'audit',
    permission: 'audit:read',
    area: 'settings',
    label: $localize`:@@shell.nav.audit:Audit log`,
  },
  {
    id: 'secrets',
    group: 'settings',
    phase: 'G2',
    order: 6,
    icon: 'key',
    scope: 'project',
    segment: 'secrets',
    permission: 'ingest_token:read',
    area: 'settings',
    label: $localize`:@@shell.nav.secrets:Secret references`,
  },
  {
    id: 'security',
    group: 'settings',
    phase: 'G2',
    order: 7,
    icon: 'shield-alert',
    scope: 'org',
    segment: 'security',
    permission: 'security_event:read',
    area: 'settings',
    label: $localize`:@@shell.nav.security:Security events`,
  },
  {
    id: 'support',
    group: 'settings',
    phase: 'G2',
    order: 8,
    icon: 'life',
    scope: 'org',
    segment: 'support',
    permission: 'support:read',
    area: 'settings',
    label: $localize`:@@shell.nav.support:Support access`,
  },
  {
    id: 'agents',
    group: 'settings',
    phase: 'G4',
    order: 9,
    icon: 'bot',
    scope: 'org',
    segment: 'agents',
    permission: 'agent_pool:read',
    area: 'settings',
    label: $localize`:@@shell.nav.agents:Agent pools`,
  },
  {
    id: 'integrations',
    group: 'settings',
    phase: 'G4',
    order: 10,
    icon: 'plug',
    scope: 'project',
    segment: 'integrations',
    permission: 'webhook:read',
    area: 'settings',
    label: $localize`:@@shell.nav.integrations:Integrations & webhooks`,
  },
  {
    id: 'tokens',
    group: 'settings',
    phase: 'G4',
    order: 11,
    icon: 'key',
    scope: 'org',
    segment: 'tokens',
    permission: 'service_identity:read',
    area: 'settings',
    label: $localize`:@@shell.nav.tokens:API tokens`,
  },
];
