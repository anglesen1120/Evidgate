import { DOCUMENT } from '@angular/common';
import { CdkTrapFocus } from '@angular/cdk/a11y';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  NavigationEnd,
  PRIMARY_OUTLET,
  Router,
  RouterLink,
  RouterOutlet,
  UrlSegment,
} from '@angular/router';
import { filter } from 'rxjs';
import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../nav/nav-view';
import { NavComponent } from './nav.component';
import { ScopeSwitcherComponent } from './scope-switcher.component';
import { ShellIconComponent } from './shell-icon.component';
import { ThemeService } from './theme.service';
import { trackTopbarHeight } from './topbar-height';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    NavComponent,
    ScopeSwitcherComponent,
    ShellIconComponent,
    HlmButton,
    HlmCard,
    CdkTrapFocus,
  ],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block bg-background font-sans text-sm text-foreground',
    '(window:resize)': 'onResize()',
  },
})
export class ShellComponent {
  readonly session = inject(SessionStore);
  readonly nav = inject(NavViewService);
  readonly theme = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly aside = viewChild<ElementRef<HTMLElement>>('navigation');
  private readonly topbar = viewChild<ElementRef<HTMLElement>>('topbar');
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');
  private readonly toggleButton = viewChild<ElementRef<HTMLButtonElement>>('toggleButton');
  private readonly main = viewChild<ElementRef<HTMLElement>>('mainContent');
  private readonly viewportWidth = signal(this.document.defaultView?.innerWidth ?? 1280);
  private readonly userCollapsed = signal<boolean | null>(null);
  readonly mobile = computed(() => this.viewportWidth() < 768);
  readonly collapsed = computed(
    () => !this.mobile() && (this.userCollapsed() ?? this.viewportWidth() < 1280),
  );
  readonly drawerOpen = signal(false);
  readonly brandLabel = $localize`:@@shell.layout.workspace:Evidgate workspace`;
  readonly closeLabel = $localize`:@@shell.layout.close-navigation:Close navigation`;
  readonly openLabel = $localize`:@@shell.layout.open-navigation:Open navigation`;
  readonly collapseLabel = $localize`:@@shell.layout.collapse-navigation:Collapse navigation`;
  readonly expandLabel = $localize`:@@shell.layout.expand-navigation:Expand navigation`;
  readonly darkLabel = $localize`:@@shell.theme.dark-mode:Dark mode`;
  /**
   * The drawer's own accessible name. The `<nav>` inside it already carries
   * `shell.layout.primary-navigation`, so the dialog must not repeat it: two landmarks with the
   * same name are announced twice with no way to tell them apart.
   */
  readonly navigationDrawerLabel = $localize`:@@shell.layout.navigation-drawer:Navigation drawer`;
  private hasNavigated = this.router.navigated;
  /** Path (no query, no fragment) of the previous NavigationEnd, for the focus handoff. */
  private lastPath: string | null = null;
  private focusRequest = 0;

  constructor() {
    // The bar is a wrapping flex row, so its height only exists after layout: measure it once the
    // shell has rendered and follow every later resize of the bar itself.
    afterNextRender(
      () => {
        const topbar = this.topbar()?.nativeElement;
        if (topbar === undefined) return;
        this.destroyRef.onDestroy(trackTopbarHeight(this.document, topbar));
      },
      { injector: this.injector },
    );
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        // A query-only or fragment-only change (a filter, a page, a tab) leaves the visitor where
        // they were, so moving focus to <main> would steal it from the control they just used.
        const path = this.pathOf(event.urlAfterRedirects);
        const moveFocus = this.hasNavigated && path !== this.lastPath;
        this.hasNavigated = true;
        this.lastPath = path;
        this.drawerOpen.set(false);
        if (moveFocus) this.focusRequest++;
        afterNextRender(
          () => {
            const main = this.main()?.nativeElement;
            if (moveFocus) main?.focus();
            const heading = main?.querySelector('h1')?.textContent?.trim();
            this.document.title = heading ? `${heading} · Evidgate` : this.brandLabel;
          },
          { injector: this.injector },
        );
      });
  }

  /** The URL's path only — query string and fragment removed, matrix parameters kept. */
  private pathOf(url: string): string {
    const primary = this.router.parseUrl(url).root.children[PRIMARY_OUTLET];
    return primary?.segments.map((segment) => segment.toString()).join('/') ?? '';
  }

  /**
   * The skip link focuses the region instead of following its fragment.
   *
   * The `href` stays for semantics, but a fragment-only URL resolves against the document's
   * `<base href>`. Every localized bundle is served from its own base (`/en/`, `/vi/`), so on a real
   * deep link the browser would treat `#main-content` as `/en/#main-content` — a different path, a
   * full document navigation, and the whole app would boot again at the landing route instead of
   * skipping the sidebar. Focus (with the scroll the user expects) does the job without a URL change.
   */
  skipToMain(event: Event): void {
    const main = this.main()?.nativeElement;
    if (main === undefined) return;
    event.preventDefault();
    main.focus();
    main.scrollIntoView?.({ block: 'start' });
  }

  onResize(): void {
    this.viewportWidth.set(this.document.defaultView?.innerWidth ?? 1280);
    if (!this.mobile() && this.drawerOpen()) this.closeDrawer();
  }

  toggleNavigation(): void {
    if (!this.mobile()) {
      this.userCollapsed.set(!this.collapsed());
      return;
    }
    this.drawerOpen.set(true);
    this.focusWhenUnblocked('drawer');
  }

  closeDrawer(restoreOpener = true): void {
    this.drawerOpen.set(false);
    if (restoreOpener) this.focusWhenUnblocked('opener');
    else this.focusRequest++;
  }

  onNavigationSelected(link: readonly string[]): void {
    if (!this.mobile()) return;
    const destination = this.router.serializeUrl(this.router.createUrlTree(link));
    this.closeDrawer(this.pathOf(destination) === this.pathOf(this.router.url));
  }

  /**
   * Focus after the inert boundary has rendered, with bounded frame retries if the browser
   * has not made the control focusable yet. Navigation and newer drawer actions invalidate pending
   * retries instead of stealing focus.
   */
  private focusWhenUnblocked(target: 'drawer' | 'opener'): void {
    const request = ++this.focusRequest;
    const focus = (framesLeft: number): void => {
      if (request !== this.focusRequest) return;
      if (target === 'drawer' ? !this.mobile() || !this.drawerOpen() : this.drawerOpen()) return;
      const element =
        target === 'drawer'
          ? this.closeButton()?.nativeElement
          : this.toggleButton()?.nativeElement;
      if (!element?.isConnected) return;
      if (!element.closest('[inert]')) element.focus();
      if (this.document.activeElement !== element && framesLeft > 0) {
        this.document.defaultView?.requestAnimationFrame(() => focus(framesLeft - 1));
      }
    };
    afterNextRender(() => focus(3), { injector: this.injector });
  }

  onDrawerKeydown(event: KeyboardEvent): void {
    if (!this.mobile() || !this.drawerOpen() || event.key !== 'Escape' || event.defaultPrevented)
      return;
    // The scope popup owns Escape before the containing navigation drawer does.
    if (this.aside()?.nativeElement.querySelector('[role="combobox"][aria-expanded="true"]'))
      return;
    event.preventDefault();
    this.closeDrawer();
  }

  /**
   * An organization change replaces the whole scope: the previous organization's project, page and
   * query string do not exist there, so only the new org id is kept (pinned in the spec).
   */
  selectOrganization(id: string): void {
    void this.router.navigate(['/orgs', id]);
  }

  /**
   * A project change stays inside the same organization, so the entry screen, its query string and
   * its fragment stay; only the project id is replaced. Deeper segments belong to the previous
   * project's data (a run id, a node id), so they are dropped instead of carried over.
   */
  selectProject(id: string): void {
    const org = this.session.currentOrg();
    if (!org) return;
    const tree = this.router.parseUrl(this.router.url);
    const primary = tree.root.children[PRIMARY_OUTLET];
    const projectIndex =
      primary?.segments.findIndex((segment) => segment.path === 'projects') ?? -1;
    const previous = primary?.segments[projectIndex + 1];
    if (primary && projectIndex >= 0 && previous) {
      const entry = primary.segments[projectIndex + 2] ?? new UrlSegment('overview', {});
      primary.segments.splice(
        projectIndex + 1,
        primary.segments.length - projectIndex - 1,
        new UrlSegment(id, previous.parameters),
        entry,
      );
      void this.router.navigateByUrl(tree);
    } else {
      void this.router.navigate(['/orgs', org.id, 'projects', id, 'overview'], {
        queryParams: tree.queryParams,
        ...(tree.fragment === null ? {} : { fragment: tree.fragment }),
      });
    }
  }
}
