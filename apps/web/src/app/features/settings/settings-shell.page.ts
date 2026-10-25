// 09.7.1-T1.1/T1.2 (AC1): route navigation exposes only phase-active, server-permitted tabs.
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ActivatedRoute,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { filter, map } from 'rxjs';

import { SessionStore } from '../../core/session/session.store';
import { availableSettingsTabs } from './settings-tabs';

@Component({
  selector: 'app-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings-shell.page.html',
  styleUrl: './settings-shell.page.css',
})
export class SettingsShellPage {
  protected readonly session = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly tabs = computed(() => {
    // Read the declared set explicitly: even an injected permission predicate may not track it.
    this.session.permissions();
    return availableSettingsTabs((code) => this.session.has(code));
  });

  protected readonly activeSegment = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.route.firstChild?.snapshot.url[0]?.path ?? ''),
    ),
    // A child's route exists before its snapshot is installed during parent activation.
    { initialValue: this.route.firstChild?.snapshot?.url[0]?.path ?? '' },
  );

  protected selectDestination(select: HTMLSelectElement): void {
    const segment = select.value;
    // Only values from the same permission-filtered list may initiate navigation.
    if (!this.tabs().some((tab) => tab.segment === segment)) {
      select.value = this.activeSegment();
      return;
    }
    // The router can refuse or abort the navigation while the browser has already moved the native
    // select to the pick: once it settles, the control shows the segment the router really serves.
    const resync = (): void => {
      select.value = this.activeSegment();
    };
    void this.router.navigate([segment], { relativeTo: this.route }).then(resync, resync);
  }
}
