// 09.1.1-T4.1 (plan 09.1.1 §10 T4; docs/design/web/layout.md): the placeholder every navigation
// entry renders until its own feature story ships a screen. It is deliberately one component:
// the route table is built from `NAV_ENTRIES`, so a new entry needs a registry row and nothing
// here, and no stub can drift from the label the sidebar shows.
//
// The entry is identified by the `entryId` the route table puts in `data` on the lazy route that
// owns the page (a feature route mounts this page at `''` under that route), so the label comes
// from the registry, never from a second hand-written list.
//
// Copy rule (D-S68-Q3): a stub says only that the screen is not built yet. It never names a
// resource, so it cannot reveal whether something exists in another org or project.

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

import type { NavEntry } from '../nav/nav.models';
import { NAV_ENTRIES } from '../nav/nav-registry';

/** The `entryId` of the closest route that carries one, or `null` when the page is mounted outside the table. */
function entryIdOf(route: ActivatedRoute): string | null {
  let found: string | null = null;
  // The snapshots, not the routes: `ActivatedRoute.data` is the observable of later data changes,
  // and the entry id is fixed for the lifetime of the activation. `pathFromRoot` walks the whole
  // chain, so the id `app.routes.ts` put on the owning lazy route is found from the page below it.
  for (const snapshot of route.snapshot.pathFromRoot) {
    // Angular types `Data` as `{ [key: string]: any }`; viewing it as `unknown` members keeps the
    // narrowing below honest instead of letting `any` leak into the page.
    const data: Readonly<Record<string, unknown>> = snapshot.data;
    const id = data['entryId'];
    if (typeof id === 'string') {
      found = id;
    }
  }
  return found;
}

@Component({
  selector: 'app-route-stub',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/shell-page.css',
  template: `<section>
    <p class="eyebrow" i18n="@@shell.stub.eyebrow">Screen not built yet</p>
    <h1>{{ title }}</h1>
    <p i18n="@@shell.stub.body">
      This screen arrives with its own feature story. Its route, permission guard and navigation
      entry are in place so the shell can be reviewed end to end.
    </p>
  </section>`,
})
export class RouteStubPage {
  private readonly route = inject(ActivatedRoute);

  private readonly entry: NavEntry | undefined = NAV_ENTRIES.find(
    (candidate) => candidate.id === entryIdOf(this.route),
  );

  /** The registry label of this entry, so the page and the sidebar can never disagree. */
  readonly title = this.entry?.label ?? $localize`:@@shell.stub.untitled:Untitled screen`;
}
