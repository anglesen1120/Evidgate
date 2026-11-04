import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { policyModeView } from './policy-mode';

@Component({
  selector: 'app-policy-mode-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <span
      hlmBadge
      data-policy-mode
      [variant]="'secondary'"
      class="max-w-full whitespace-normal wrap-anywhere"
      [class]="tone()"
      [attr.data-mode]="view().kind === 'unavailable' ? 'unavailable' : mode()"
    >
      @switch (view().kind) {
        @case ('gating') {
          {{ copy.gating }}: {{ mode() }}
        }
        @case ('advisory') {
          {{ copy.advisory }}: {{ mode() }}
        }
        @case ('unknown') {
          {{ copy.mode }}: {{ mode() }}
        }
        @default {
          {{ copy.unavailable }}
        }
      }
    </span>
  `,
})
export class PolicyModeChipComponent {
  readonly mode = input<string | null | undefined>(null);
  protected readonly view = computed(() => policyModeView(this.mode()));
  protected readonly tone = computed(() => {
    switch (this.view().kind) {
      case 'gating':
        return 'bg-[var(--eg-info-bg)] text-[var(--eg-info-fg)]';
      case 'advisory':
        return 'bg-[var(--eg-warning-bg)] text-[var(--eg-warning-fg)]';
      default:
        return 'bg-[var(--eg-neutral-bg)] text-[var(--eg-neutral-fg)]';
    }
  });
  protected readonly copy = {
    gating: $localize`:@@settings.policy.gating:Gating`,
    advisory: $localize`:@@settings.policy.advisory:Advisory`,
    mode: $localize`:@@settings.policy.mode:Mode`,
    unavailable: $localize`:@@settings.policy.mode-unavailable:Mode unavailable`,
  };
}
