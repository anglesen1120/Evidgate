import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
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
import { HlmButton } from '@evidgate/ui/button';
import { HlmSelectImports } from '@evidgate/ui/select';
import { BrnSelect } from '@spartan-ng/brain/select';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmCardImports } from '@evidgate/ui/card';

import { SessionStore } from '../../core/session/session.store';
import { availableSettingsTabs } from './settings-tabs';
import { RetentionFormComponent } from './quota-retention/retention-form.component';

@Component({
  selector: 'app-settings-shell',
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    HlmButton,
    HlmSelectImports,
    HlmLabel,
    HlmCardImports,
    RetentionFormComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings-shell.page.html',
  host: { class: 'grid min-w-0 gap-6 font-sans text-foreground' },
})
export class SettingsShellPage {
  protected readonly session = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly destination = viewChild(BrnSelect);
  protected readonly pageHeadingSize = 'page' as const;
  protected readonly choose = $localize`:@@settings.choose:Choose a settings destination`;
  protected readonly destinationLabel = (value: unknown): string =>
    this.tabs().find((tab) => tab.segment === value)?.label ?? this.choose;
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

  protected selectDestination(segment: unknown): void {
    if (segment === this.activeSegment()) return;
    const resync = (): void => {
      this.destination()?.value.set(this.activeSegment());
    };
    // The permission-filtered registry and the routed segment remain authoritative on refusal.
    if (typeof segment !== 'string' || !this.tabs().some((tab) => tab.segment === segment)) {
      resync();
      return;
    }
    void this.router.navigate([segment], { relativeTo: this.route }).then(resync, resync);
  }
}
