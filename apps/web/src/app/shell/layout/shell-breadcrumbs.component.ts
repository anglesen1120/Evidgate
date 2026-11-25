import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { PRIMARY_OUTLET, Router, RouterLink } from '@angular/router';
import { middleTruncate } from '../../shared/ui/values/middle-truncate';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';
import { RunBreadcrumbItem } from './shell-breadcrumbs.run-item';
import { HlmIcon } from '@evidgate/ui/icon';
import { SessionStore } from '../../core/session/session.store';
import { NAV_ENTRIES } from '../nav/nav-registry';
import { NavViewService } from '../nav/nav-view';

@Component({
  selector: 'app-shell-breadcrumbs',
  imports: [HlmIcon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0', '[class.hidden]': '!trail().parts.length' },
  template: `
    @if (trail().parts.length) {
      <nav aria-label="Breadcrumb" i18n-aria-label="@@shell.breadcrumb.label">
        <ol
          class="m-0 flex min-w-0 list-none flex-wrap items-center gap-x-(--eg-space-2) gap-y-(--eg-space-1) p-0 text-xs leading-(--eg-leading-xs) text-muted-foreground"
        >
          @for (part of trail().parts; track $index; let first = $first, last = $last) {
            <li class="flex min-w-0 max-w-full items-baseline gap-(--eg-space-2)">
              @if (!first) {
                <hlm-icon [name]="'lucideChevronRight'" class="size-(--eg-text-xs) self-center" />
              }
              @if (trail().sectionLink && $index === trail().parts.length - 2) {
                <a
                  [routerLink]="trail().sectionLink"
                  [queryParams]="{ tab: null, evidence_revision: null }"
                  [queryParamsHandling]="'merge'"
                  class="inline-flex min-h-6 items-center text-primary underline-offset-2 hover:underline max-md:min-h-(--eg-control-touch) pointer-coarse:min-h-(--eg-control-touch)"
                  ><bdi>{{ part }}</bdi></a
                >
              } @else {
                <bdi
                  class="min-w-0 [overflow-wrap:anywhere] aria-[current=page]:font-bold aria-[current=page]:text-foreground"
                  [attr.aria-current]="last && trail().hasPage ? 'page' : null"
                  >{{ part }}</bdi
                >
              }
            </li>
          }
        </ol>
      </nav>
    }
  `,
})
export class ShellBreadcrumbsComponent {
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  private readonly runItem = inject(RunBreadcrumbItem);
  private readonly nav = inject(NavViewService);

  protected readonly trail = computed(() => {
    const url = this.router.lastSuccessfulNavigation()?.finalUrl;
    let leaf = this.router.routerState.snapshot.root;
    while (leaf.firstChild) leaf = leaf.firstChild;
    // An unresolved address has no hierarchy; even a valid prefix must not distinguish one 404 from another.
    if (leaf.data['hideBreadcrumbs'] === true)
      return { hasPage: false, parts: [], sectionLink: null };
    const path = url?.root.children[PRIMARY_OUTLET]?.segments.map((segment) => segment.path) ?? [];
    const items = this.nav.groups().flatMap((group) => group.items);
    const current = items.find((item) =>
      item.link.every((part, index) => (index === 0 ? part.slice(1) : part) === path[index]),
    );
    const tail = path.slice(current?.link.length ?? 0);
    const detail = current?.details?.find(
      (page) =>
        page.path.length === tail.length &&
        page.path.every((part, index) => part.startsWith(':') || tail[index] === part),
    );
    // A settings tab uses its own route guard, not the sidebar row's permission. Never infer a
    // tab from user-controlled URL text: only an activated tab can contribute its label.
    const tab: unknown = current?.id === 'settings' ? leaf.data['settingsTabId'] : undefined;
    const tabLabel =
      typeof tab === 'string'
        ? NAV_ENTRIES.find((entry) => entry.settingsTabId === tab)?.label
        : undefined;
    if (leaf.data['runBreadcrumb'] === true) {
      const id = this.runItem.id();
      const item =
        id && id === leaf.paramMap.get('runId') ? middleTruncate(escapeFormatChars(id), 11) : null;
      return {
        hasPage: !!item,
        sectionLink: item ? ['/', ...path.slice(0, -1)] : null,
        parts: [
          this.session.currentOrg()?.name,
          this.session.currentProject()?.name,
          $localize`:@@shell.palette.group-runs:Runs`,
          item,
        ].filter((part): part is string => !!part),
      };
    }
    const page = detail?.label ?? tabLabel ?? leaf.title;
    const parent = current?.parentId ? items.find((item) => item.id === current.parentId) : null;
    return {
      sectionLink: null,
      hasPage: !!(current || page),
      parts: [
        this.session.currentOrg()?.name,
        this.session.currentProject()?.name,
        parent?.label,
        current?.label,
        page === current?.label ? undefined : page,
      ].filter((part): part is string => !!part),
    };
  });
}
