import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';

import { SessionStore } from '../../core/session/session.store';
import { AbsoluteTimeComponent } from '../../shared/time/absolute-time.component';
import { PageHeaderComponent } from '../../shared/ui/page-header';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';
import { ShellIconComponent } from '../../shell/layout/shell-icon.component';
import { NotFoundPatternComponent } from '../../shared/ui/states/not-found-pattern.component';
import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';
import { LoadingPatternComponent } from '../../shared/ui/states/loading-pattern.component';
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
    AbsoluteTimeComponent,
    HlmBadge,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    PageHeaderComponent,
    ShellIconComponent,
    NotFoundPatternComponent,
    EmptyPatternComponent,
    LoadingPatternComponent,
    SwitchScopeControl,
    SwitchScopeHierarchy,
    DesktopRequiredNoticeComponent,
  ],
  providers: [HealingSwitchStore],
  templateUrl: './healing-switch.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'grid min-w-0 grid-cols-1 gap-(--eg-space-3) text-sm text-foreground wrap-anywhere',
  },
})
export class HealingSwitchPage {
  private readonly narrowPolicy = inject(NarrowMutationPolicy);

  protected readonly store = inject(HealingSwitchStore);

  protected readonly session = inject(SessionStore);

  protected readonly heading = $localize`:@@healingswitch.availability:Healing availability`;
  protected readonly introduction = $localize`:@@healingswitch.inspect-scopes:Inspect the most restrictive scope before changing a healing setting.`;
  protected readonly sectionHeadingSize = 'subsection' as const;
  protected readonly copy = HEALING_SWITCH_COPY;
  protected readonly modeLabel = healingModeLabel;
  protected readonly displayIdentifier = escapeFormatChars;
  protected readonly scopeContext = computed(() => {
    const project = this.session.currentProject();
    return [this.session.currentOrg()?.name, project?.name, project?.id]
      .filter((part): part is string => !!part)
      .map(escapeFormatChars)
      .join(' · ');
  });

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
    event: {
      mode: SettableMode;
      reason: string | null;
      action?: 'healing-kill-switch' | undefined;
    },
  ): void {
    const action =
      event.mode === 'disabled' && event.action === 'healing-kill-switch'
        ? event.action
        : undefined;
    if (!this.narrowPolicy.allowsMutation(action)) return;
    void this.store.save(scope, event.mode, event.reason);
  }
}
