import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../../shared/ui/chips/status-pill.component';
import { RUN_STATE_TONES, type RunState } from '../../../../shared/ui/chips/run-state-tone';
import { runTimelineSteps, type TimelineStepStatus } from './run-timeline-steps';

/**
 * Collection lifecycle of one run as an ordered list. The run detail response carries no
 * transition history, so only the current state and the states the main path guarantees are
 * marked; step timestamps are never invented.
 */
@Component({
  selector: 'app-run-timeline',
  imports: [StatusPillComponent, HlmCard, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-timeline.component.html',
  host: { class: 'block min-w-0 text-foreground wrap-anywhere' },
})
export class RunTimelineComponent {
  readonly state = input.required<RunStateDto['state']>();
  /** Events stored with `after_cancel = true`; null when the response does not serve a count. */
  readonly afterCancelEvents = input<number | null>(null);

  protected readonly steps = computed(() => runTimelineSteps(this.state()));
  protected readonly canceled = computed(() => this.state() === 'canceled');

  // Same custom ids as the shared run-state pill so both surfaces share one translation.
  protected readonly stepLabels: Readonly<Record<RunState, string>> = {
    created: $localize`:@@shared.run-state.created:Created`,
    queued: $localize`:@@shared.run-state.queued:Queued`,
    running: $localize`:@@shared.run-state.running:Running`,
    collecting: $localize`:@@shared.run-state.collecting:Collecting`,
    complete: $localize`:@@shared.run-state.complete:Complete`,
    incomplete: $localize`:@@shared.run-state.incomplete:Incomplete`,
    canceled: $localize`:@@shared.run-state.canceled:Canceled`,
  };

  protected readonly statusLabels: Readonly<Record<TimelineStepStatus, string>> = {
    reached: $localize`:@@runs.detail.timeline.reached:Reached`,
    current: $localize`:@@runs.detail.timeline.current:Current step`,
    upcoming: $localize`:@@runs.detail.timeline.upcoming:Not reached yet`,
    'not-reached': $localize`:@@runs.detail.timeline.not-reached:Not reached`,
    unrecorded: $localize`:@@runs.detail.timeline.unrecorded:Not recorded`,
  };

  protected readonly copy = {
    title: $localize`:@@runs.detail.timeline:Run state timeline`,
    stepStatus: $localize`:@@runs.detail.timeline.step-status:Step status`,
    stepTimes: $localize`:@@runs.detail.timeline.step-times:Step times are not available yet`,
    afterCancelNote: $localize`:@@runs.detail.timeline.after-cancel-note:Events that arrive after cancel are kept as evidence and never complete the run.`,
    afterCancelEvents: $localize`:@@runs.detail.timeline.after-cancel-events:After-cancel events`,
    afterCancelNone: $localize`:@@runs.detail.timeline.after-cancel-none:No events after cancel`,
  };

  protected readonly afterCancelLabel = computed(() => {
    const count = this.afterCancelEvents();
    if (count === null) return null;
    if (count <= 0) return this.copy.afterCancelNone;
    return $localize`:@@runs.detail.timeline.after-cancel-count:${count}:count: events arrived after cancel (informational)`;
  });

  protected stepLabel(state: RunState): string {
    return this.stepLabels[state];
  }

  protected statusLabel(status: TimelineStepStatus): string {
    return this.statusLabels[status];
  }

  protected stepTone(status: TimelineStepStatus, state: RunState): StatusTone {
    if (status === 'current') return RUN_STATE_TONES[state];
    return status === 'reached' ? 'success' : 'neutral';
  }
}
