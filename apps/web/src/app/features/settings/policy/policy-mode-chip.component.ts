import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { policyModeView } from './policy-mode';

@Component({
  selector: 'app-policy-mode-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './policy.page.css',
  template: `
    <span
      class="badge"
      data-policy-mode
      [class.mode-gating]="view().kind === 'gating'"
      [class.mode-advisory]="view().kind === 'advisory'"
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
  protected readonly copy = {
    gating: $localize`:@@settings.policy.gating:Gating`,
    advisory: $localize`:@@settings.policy.advisory:Advisory`,
    mode: $localize`:@@settings.policy.mode:Mode`,
    unavailable: $localize`:@@settings.policy.mode-unavailable:Mode unavailable`,
  };
}
