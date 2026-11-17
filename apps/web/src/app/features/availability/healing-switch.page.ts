import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

import { SessionStore } from '../../core/session/session.store';
import { NotFoundPatternComponent } from '../../shared/ui/states/not-found-pattern.component';
import { DesktopRequiredNoticeComponent } from '../../shared/ui/narrow/desktop-required-notice.component';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import {
  HEALING_SWITCH_COPY,
  healthLabel,
  modeLabel as healingModeLabel,
} from './healing-switch.copy';
import { HealingSwitchStore, type SettableMode, type SwitchScope } from './healing-switch.store';
import { SwitchScopeControl } from './switch-scope-control.component';
import { SwitchScopeHierarchy } from './switch-scope-hierarchy.component';

/**
 * The Availability screen: the healing kill switch as three stored scopes and the resolved
 * posture. The store owns the read and the write; this page only decides which state is on
 * screen and which scope a submission names.
 */
@Component({
  selector: 'app-healing-switch-page',
  imports: [
    HlmButton,
    HlmCardImports,
    NotFoundPatternComponent,
    SwitchScopeControl,
    SwitchScopeHierarchy,
    DesktopRequiredNoticeComponent,
  ],
  providers: [HealingSwitchStore],
  templateUrl: './healing-switch.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 text-sm text-foreground wrap-anywhere' },
})
export class HealingSwitchPage {
  private readonly narrowPolicy = inject(NarrowMutationPolicy);

  protected readonly store = inject(HealingSwitchStore);

  protected readonly session = inject(SessionStore);

  protected readonly pageHeadingSize = 'page' as const;
  protected readonly sectionHeadingSize = 'section' as const;
  protected readonly copy = HEALING_SWITCH_COPY;
  protected readonly modeLabel = healingModeLabel;

  /** The write controls need both the permission and a resolution whose modes are settable. */
  protected readonly canEdit = computed(
    () => this.store.canConfigure() && this.store.resolution()?.resolved !== 'unavailable',
  );
  protected readonly hasWritableScope = computed(
    () =>
      this.canEdit() &&
      (this.store.blockedBy('tenant') === null || this.store.blockedBy('project') === null),
  );

  protected canRead(): boolean {
    return this.session.has('healing:triage');
  }

  /** The label for one observation, or null when the value is unknown and must speak for itself. */
  protected healthText(value: string | null): string | null {
    return value === null ? this.copy.notObserved : healthLabel(value);
  }

  protected deciderCopy(scope: SwitchScope | null): string {
    switch (scope) {
      case 'system':
        return this.copy.decidedBySystem;
      case 'tenant':
        return this.copy.decidedByTenant;
      case 'project':
        return this.copy.decidedByProject;
      default:
        return this.copy.resolvedDefault;
    }
  }

  protected submit(
    scope: 'tenant' | 'project',
    event: { mode: SettableMode; reason: string | null },
  ): void {
    if (!this.narrowPolicy.allowsMutation()) return;
    void this.store.save(scope, event.mode, event.reason);
  }
}
