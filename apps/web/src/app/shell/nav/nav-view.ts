// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1, §5; CR-D-2 navigation rule; D-S68-Q1/Q4): the sidebar's view
// model — what `NavComponent` renders, in the order it renders it.
//
// Three filters and nothing else:
//
// 1. **phase** — an entry of a phase the workspace has not reached is absent (hidden, not
//    disabled; CR-D-2). `phaseGuard` refuses its route with the same predicate.
// 2. **permission** — an entry whose code the session's set does not hold is absent. The set is a
//    convenience built from server-declared codes (D-S68-Q1); the API's own `403` remains the
//    real gate, so hiding an entry never grants anything and showing one never bypasses anything.
// 3. **scope** — a project-scope entry needs a resolved project and an org-scope entry a resolved
//    org, because its link is built from those ids. Before the scope resolves the sidebar is empty
//    rather than wrong.
//
// A group with no visible item is dropped, so the sidebar never renders a header over nothing.

import { computed, inject, Injectable, type Signal } from '@angular/core';

import { SessionStore } from '../../core/session/session.store';
import type { NavEntry, NavGroupView, NavItemView } from './nav.models';
import { NAV_ENTRIES, NAV_GROUPS } from './nav-registry';
import { isPhaseActive } from './product-phase';

/** One entry that survived the filters, with the router commands it renders (D-S68-Q4). */
interface VisibleEntry {
  readonly entry: NavEntry;
  readonly link: readonly string[];
}

@Injectable({ providedIn: 'root' })
export class NavViewService {
  private readonly session = inject(SessionStore);

  /**
   * The groups the sidebar renders for the current session and scope, groups in journey order and
   * items in registry order. Empty until an org is resolved — the shell shows the workspace
   * switcher and the landing guard picks the destination.
   *
   * Each entry is filtered and linked in one pass, so the link is built only where its ids are
   * known: a project-scope entry gets its `/orgs/:orgId/projects/:projectId/<segment>` commands
   * from the resolved project, and the org-scope form from the resolved org.
   */
  readonly groups: Signal<readonly NavGroupView[]> = computed(() => {
    const org = this.session.currentOrg();
    if (org === null) {
      return [];
    }
    const projectId = this.session.currentProject()?.id ?? null;

    const visible = NAV_ENTRIES.flatMap((entry): VisibleEntry[] => {
      if (!isPhaseActive(entry.phase) || !this.session.has(entry.permission)) {
        return [];
      }
      if (entry.scope === 'org') {
        return [{ entry, link: ['/orgs', org.id, entry.segment] }];
      }
      return projectId === null
        ? []
        : [{ entry, link: ['/orgs', org.id, 'projects', projectId, entry.segment] }];
    });

    const groups: NavGroupView[] = [];
    for (const group of NAV_GROUPS) {
      const items: NavItemView[] = visible
        .filter(({ entry }) => entry.group === group.id)
        .sort((left, right) => left.entry.order - right.entry.order)
        .map(({ entry, link }) => ({
          id: entry.id,
          label: entry.label,
          icon: entry.icon,
          link,
        }));
      if (items.length > 0) {
        groups.push({ id: group.id, label: group.label, items });
      }
    }
    return groups;
  });
}
