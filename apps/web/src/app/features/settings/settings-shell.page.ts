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

import { PageHeaderComponent } from '../../shared/ui/page-header';
import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../../shell/nav/nav-view';
import { availableSettingsTabs } from './settings-tabs';
import { RetentionFormComponent } from './quota-retention/retention-form.component';

@Component({
  selector: 'app-settings-shell',
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    PageHeaderComponent,
    RetentionFormComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings-shell.page.html',
  host: { class: 'grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4 font-sans text-foreground' },
})
export class SettingsShellPage {
  protected readonly session = inject(SessionStore);
  /** The Settings strip grouped by section: the model the sidebar's Settings entry covers. */
  protected readonly sections = inject(NavViewService).settingsSections;
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly title = $localize`:@@settings.title:Settings`;
  protected readonly description = $localize`:@@settings.shell-intro:Open a section to manage settings for your organization or project.`;
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
  protected readonly activeTab = computed(() =>
    this.tabs().find((tab) => tab.segment === this.activeSegment()),
  );

  /** Keyboard focus must bring a clipped tab fully into the scrolling strip. */
  protected reveal(event: FocusEvent): void {
    (event.target as HTMLElement).scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }
}
