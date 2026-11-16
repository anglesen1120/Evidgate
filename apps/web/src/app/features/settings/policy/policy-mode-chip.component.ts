import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../shared/ui/chips/status-pill.component';
import { policyModeView } from './policy-mode';

@Component({
  selector: 'app-policy-mode-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusPillComponent],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <span
      data-policy-mode
      [attr.data-mode]="view().kind === 'unavailable' ? 'unavailable' : mode()"
    >
      <app-status-pill [tone]="tone()" [label]="label()" />
      @if (mode() && view().kind !== 'unavailable') {
        <code class="block font-mono wrap-anywhere" data-policy-mode-raw>{{ mode() }}</code>
      }
    </span>
  `,
})
export class PolicyModeChipComponent {
  readonly mode = input<string | null | undefined>(null);
  protected readonly view = computed(() => policyModeView(this.mode()));
  protected readonly tone = computed<StatusTone>(() => {
    switch (this.view().kind) {
      case 'gating':
        return 'info';
      case 'advisory':
        return 'warning';
      default:
        return 'neutral';
    }
  });
  protected readonly label = computed(() => {
    switch (this.view().kind) {
      case 'gating':
        return this.copy.gating;
      case 'advisory':
        return this.copy.advisory;
      case 'unknown':
        return this.copy.unknown;
      default:
        return this.copy.unavailable;
    }
  });
  protected readonly copy = {
    gating: $localize`:@@settings.policy.gating:Gating`,
    advisory: $localize`:@@settings.policy.advisory:Advisory`,
    unknown: $localize`:@@settings.policy.mode-unknown:Unknown mode`,
    unavailable: $localize`:@@settings.policy.mode-unavailable-short:Unavailable`,
  };
}
