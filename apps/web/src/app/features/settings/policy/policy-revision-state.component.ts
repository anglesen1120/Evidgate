import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../shared/ui/chips/status-pill.component';

@Component({
  selector: 'app-policy-revision-state',
  imports: [StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <app-status-pill [tone]="tone()" [label]="label()" />
    @if (state() !== 'active' && state() !== 'retired' && state() !== 'draft') {
      <code class="block font-mono wrap-anywhere">{{ state() }}</code>
    }
  `,
})
export class PolicyRevisionStateComponent {
  readonly state = input.required<string>();
  protected readonly tone = computed<StatusTone>(() =>
    this.state() === 'active' ? 'info' : 'neutral',
  );
  protected readonly label = computed(() => {
    switch (this.state()) {
      case 'active':
        return $localize`:@@settings.policy.state-active:Active`;
      case 'retired':
        return $localize`:@@settings.policy.state-retired:Retired`;
      case 'draft':
        return $localize`:@@settings.policy.state-draft:Draft`;
      default:
        return $localize`:@@settings.policy.state-unknown:Unknown state`;
    }
  });
}
