import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

import { IDENTITY_BADGE_CLASSES } from './badge-classes';

@Component({
  selector: 'app-revision-chip',
  imports: [HlmBadge],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <span hlmBadge [variant]="'outline'" class="identity-chip revision-chip" [class]="badgeClasses">
      @if (hasRevision()) {
        @if (label()) {
          <span class="identity-chip__label min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
            label()
          }}</span>
        }
        <span class="identity-chip__value min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
          identity()
        }}</span>
      } @else {
        <span
          class="identity-chip__label min-w-0 whitespace-normal [overflow-wrap:anywhere]"
          i18n="@@shared.revision-chip.unavailable"
        >
          Revision unavailable
        </span>
      }
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RevisionChipComponent {
  readonly revision = input.required<string | number>();
  /** Localized field context; callers can distinguish evidence, policy or case revision. */
  readonly label = input($localize`:@@shared.revision-chip.label:Revision`);
  protected readonly badgeClasses = IDENTITY_BADGE_CLASSES;

  protected readonly hasRevision = computed(() => {
    const revision = this.revision();
    if (typeof revision === 'number') {
      return Number.isInteger(revision) && revision > 0;
    }
    return revision !== null && revision !== undefined && String(revision).trim().length > 0;
  });
  // Keep the supplied identifier locale-independent; r is an identity prefix, not prose.
  protected readonly identity = computed(() => `r${this.revision()}`);
}
