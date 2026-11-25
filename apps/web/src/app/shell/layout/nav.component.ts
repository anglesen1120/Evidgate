import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { filter } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import type { NavGroupView, NavSettingsItem } from '../nav/nav.models';
import { ShellIconComponent } from './shell-icon.component';

/** Consumes the already-filtered nav view; no permission or phase decisions belong here. */
@Component({
  selector: 'app-shell-nav',
  imports: [RouterLink, RouterLinkActive, ShellIconComponent, HlmButton, HlmTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nav.component.html',
  styles: `
    a[aria-current='page']::before {
      content: '';
      position: absolute;
      inset: 0;
      border-inline-start: calc(3 * var(--eg-border-width)) solid var(--eg-accent);
      border-radius: inherit;
      pointer-events: none;
    }
  `,
  host: {
    class:
      'group/nav block min-h-0 overflow-y-auto overscroll-contain scroll-py-(--eg-space-3) px-(--eg-space-4) data-[collapsed=true]:px-(--eg-space-3) pb-(--eg-space-5) [scrollbar-width:thin]',
    '[class.rail]': 'collapsed()',
    '[attr.data-collapsed]': 'collapsed()',
  },
})
export class NavComponent {
  private readonly router = inject(Router);
  readonly groups = input<readonly NavGroupView[]>([]);
  readonly collapsed = input(false);
  readonly settings = input<NavSettingsItem | null>(null);
  readonly itemSelected = output<readonly string[]>();
  readonly navigationLabel = $localize`:@@shell.layout.primary-navigation:Primary navigation`;
  private readonly navigated = toSignal(
    this.router.events.pipe(filter((event) => event instanceof NavigationEnd)),
    { initialValue: null },
  );
  // The Settings item covers every tab URL; `subset` keeps it lit on each of them.
  readonly settingsActive = computed(() => {
    const item = this.settings();
    this.navigated();
    return (
      item !== null &&
      item.covers.some((link) =>
        this.router.isActive(this.router.createUrlTree(link), {
          paths: 'subset',
          queryParams: 'ignored',
          fragment: 'ignored',
          matrixParams: 'ignored',
        }),
      )
    );
  });
}
