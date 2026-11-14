import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { isRunState, RUN_STATE_TONES, type RunState } from './run-state-tone';
import { StatusPillComponent, type StatusTone } from './status-pill.component';

const RUN_STATE_LABELS: Readonly<Record<RunState, string>> = {
  created: $localize`:@@shared.run-state.created:Created`,
  queued: $localize`:@@shared.run-state.queued:Queued`,
  running: $localize`:@@shared.run-state.running:Running`,
  collecting: $localize`:@@shared.run-state.collecting:Collecting`,
  complete: $localize`:@@shared.run-state.complete:Complete`,
  incomplete: $localize`:@@shared.run-state.incomplete:Incomplete`,
  canceled: $localize`:@@shared.run-state.canceled:Canceled`,
};

/** Collection lifecycle only; compact tables already name the dimension in their column header. */
@Component({
  selector: 'app-run-state-pill',
  imports: [StatusPillComponent],
  template: `
    <app-status-pill [tone]="tone()" [label]="label()" [dimension]="compact() ? '' : dimension" />
  `,
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunStatePillComponent {
  readonly state = input.required<string>();
  readonly compact = input(false);

  protected readonly dimension = $localize`:@@shared.run-state.dimension:Run status`;
  protected readonly tone = computed<StatusTone>(() => {
    const state = this.state();
    return isRunState(state) ? RUN_STATE_TONES[state] : 'neutral';
  });
  protected readonly label = computed(() => {
    const state = this.state();
    if (!state?.trim()) {
      return $localize`:@@shared.run-state.unavailable:Run status unavailable`;
    }
    return isRunState(state) ? RUN_STATE_LABELS[state] : state;
  });
}
