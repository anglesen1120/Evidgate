import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
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

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, NavComponent, ScopeSwitcherComponent, ShellIconComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:resize)': 'onResize()' },
})
export class ShellComponent {
  readonly session = inject(SessionStore);
  readonly nav = inject(NavViewService);
  readonly theme = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly aside = viewChild<ElementRef<HTMLElement>>('navigation');
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
  private hasNavigated = this.router.navigated;

  constructor() {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        const moveFocus = this.hasNavigated;
        this.hasNavigated = true;
        this.drawerOpen.set(false);
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
    afterNextRender(() => this.closeButton()?.nativeElement.focus(), { injector: this.injector });
  }

  closeDrawer(): void {
    this.drawerOpen.set(false);
    // Defer until inert is removed from the opener's main-stack ancestor.
    afterNextRender(() => this.toggleButton()?.nativeElement.focus(), { injector: this.injector });
  }

  onDrawerKeydown(event: KeyboardEvent): void {
    if (!this.mobile() || !this.drawerOpen()) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closeDrawer();
    } else if (event.key === 'Tab') {
      const controls = Array.from(
        this.aside()?.nativeElement.querySelectorAll<HTMLElement>(
          'button:not(:disabled), a[href], select:not(:disabled), [tabindex="0"]',
        ) ?? [],
      );
      const first = controls[0];
      const last = controls.at(-1);
      if (event.shiftKey && this.document.activeElement === first) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && this.document.activeElement === last) {
        event.preventDefault();
        first?.focus();
      }
    }
  }

  selectOrganization(id: string): void {
    void this.router.navigate(['/orgs', id]);
  }

  selectProject(id: string): void {
    const org = this.session.currentOrg();
    if (!org) return;
    const tree = this.router.parseUrl(this.router.url);
    const primary = tree.root.children[PRIMARY_OUTLET];
    const projectIndex =
      primary?.segments.findIndex((segment) => segment.path === 'projects') ?? -1;
    const previous = primary?.segments[projectIndex + 1];
    if (primary && projectIndex >= 0 && previous) {
      primary.segments[projectIndex + 1] = new UrlSegment(id, previous.parameters);
      if (primary.segments.length === projectIndex + 2) {
        primary.segments.push(new UrlSegment('overview', {}));
      }
      void this.router.navigateByUrl(tree);
    } else {
      void this.router.navigate(['/orgs', org.id, 'projects', id, 'overview'], {
        queryParams: tree.queryParams,
        ...(tree.fragment === null ? {} : { fragment: tree.fragment }),
      });
    }
  }
}
