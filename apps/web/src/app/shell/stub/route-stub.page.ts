// Unbuilt routes use the registry label without echoing potentially protected resource details.

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmCard, HlmCardTitle, HlmCardDescription } from '@evidgate/ui/card';

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
  imports: [HlmBadge, HlmCard, HlmCardTitle, HlmCardDescription],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `<section
    hlmCard
    class="min-w-0 items-start gap-(--eg-space-4) px-(--eg-space-4) py-(--eg-space-8) [overflow-wrap:anywhere] min-[768px]:px-(--eg-space-5)"
    aria-labelledby="route-stub-heading"
  >
    <p
      hlmBadge
      [variant]="'secondary'"
      class="eyebrow m-0 whitespace-normal"
      i18n="@@shell.stub.eyebrow"
    >
      Screen not built yet
    </p>
    <h1
      hlmCardTitle
      class="m-0 font-(family-name:--eg-font-display) text-xl font-(weight:--eg-weight-bold)"
      id="route-stub-heading"
    >
      {{ title }}
    </h1>
    <p hlmCardDescription class="m-0" i18n="@@shell.stub.body">
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
