import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { escapeFormatChars } from '../values/ref-name-display';
import { isRunState, RUN_STATE_TONES, type RunState } from './run-state-tone';
import { StatusPillComponent, type StatusTone } from './status-pill.component';

const RUN_STATE_LABELS: Readonly<Record<RunState, string>> = {
  created: $localize`:@@shared.run-state.short.created:Created`,
  queued: $localize`:@@shared.run-state.short.queued:Queued`,
  running: $localize`:@@shared.run-state.short.running:Running`,
  collecting: $localize`:@@shared.run-state.short.collecting:Collecting`,
  complete: $localize`:@@shared.run-state.short.complete:Complete`,
  incomplete: $localize`:@@shared.run-state.short.incomplete:Incomplete`,
  canceled: $localize`:@@shared.run-state.short.canceled:Canceled`,
};

const RUN_STATE_DETAILS: Readonly<Record<RunState, string>> = {
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
    <app-status-pill
      [tone]="tone()"
      [label]="label()"
      [dimension]="compact() ? '' : dimension"
      [fullValue]="unknown() ? detail() : null"
      [detail]="unknown() || showDetail() ? detail() : ''"
    />
  `,
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunStatePillComponent {
  readonly state = input.required<string>();
  readonly compact = input(false);
  readonly showDetail = input(false);

  // An unknown server value is provenance, not optional explanatory copy.
  protected readonly unknown = computed(() => {
    const state = this.state();
    return Boolean(state?.trim()) && !isRunState(state);
  });

  protected readonly dimension = $localize`:@@shared.run-state.dimension:Run status`;
  protected readonly tone = computed<StatusTone>(() => {
    const state = this.state();
    return isRunState(state) ? RUN_STATE_TONES[state] : 'neutral';
  });
  protected readonly label = computed(() => {
    const state = this.state();
    if (!state?.trim()) {
      return $localize`:@@shared.run-state.short-unavailable:Unavailable`;
    }
    return isRunState(state)
      ? RUN_STATE_LABELS[state]
      : $localize`:@@shared.run-state.short-unknown:Unknown`;
  });
  readonly detail = computed(() => {
    const state = this.state();
    if (!state?.trim()) {
      return $localize`:@@shared.run-state.unavailable:Run status unavailable`;
    }
    return isRunState(state)
      ? `${this.dimension}: ${RUN_STATE_DETAILS[state]}`
      : $localize`:@@shared.run-state.unknown-detail:Unknown run status: ${escapeFormatChars(state)}:state:`;
  });
}
