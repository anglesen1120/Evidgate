import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
} from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmAlert } from '@evidgate/ui/alert';
import { AbsoluteTimeComponent } from '../../../../shared/time/absolute-time.component';
import { formatDuration } from '../../../../shared/time/format-duration';
import { rfc3339InstantMs } from '../../../../shared/time/rfc3339-instant';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../../shared/ui/chips/status-pill.component';
import { RUN_STATE_TONES, type RunState } from '../../../../shared/ui/chips/run-state-tone';
import { runTimelineSteps, type TimelineStep, type TimelineStepStatus } from './run-timeline-steps';
import { RUN_DETAIL_NOT_PROVIDED } from '../run-value-label';

/** Served transitions supply step times; legacy runs retain an explicitly unknown history. */
@Component({
  selector: 'app-run-timeline',
  imports: [AbsoluteTimeComponent, StatusPillComponent, HlmCard, HlmCardTitle, HlmAlert],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-timeline.component.html',
  host: { class: 'block min-w-0 text-foreground wrap-anywhere' },
})
export class RunTimelineComponent {
  private readonly locale = inject(LOCALE_ID);
  readonly state = input.required<RunStateDto['state']>();
  readonly stateHistory = input<RunStateDto['state_history']>(null);
  /** Events stored with `after_cancel = true`; null when the response does not serve a count. */
  readonly afterCancelEvents = input<number | null>(null);
  protected readonly sectionSize = 'section' as const;

  protected readonly steps = computed(() => {
    const history = this.stateHistory();
    const milestones: (TimelineStep & { elapsed: string | null })[] = [];
    let previous: number | null = null;
    for (const step of runTimelineSteps(this.state(), history)) {
      // An inferred prior state is not an observed transition.
      if (step.status === 'reached' && (step.state === 'created' || history === null)) continue;
      const instant = step.at === null ? null : rfc3339InstantMs(step.at);
      const elapsed =
        previous !== null && instant !== null
          ? formatDuration(instant - previous, this.locale)
          : null;
      milestones.push({ ...step, elapsed });
      // Skipped states aren't transitions; a reached state without a time breaks the chain.
      if (step.status === 'reached' || step.status === 'current') previous = instant;
    }
    return milestones;
  });
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
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    historyUnavailable: $localize`:@@runs.detail.timeline.history-unavailable:History unavailable for this run`,
    historyEmpty: $localize`:@@runs.detail.timeline.history-empty:No state changes recorded yet`,
    elapsed: $localize`:@@runs.detail.timeline.since-previous:Since the previous transition:`,
    afterCancelNote: $localize`:@@runs.detail.timeline.after-cancel-note:Events that arrive after cancel are kept as evidence and never complete the run.`,
    afterCancelEvents: $localize`:@@runs.detail.timeline.after-cancel-events:After-cancel events`,
    afterCancelNone: $localize`:@@runs.detail.timeline.after-cancel-none:No events after cancel`,
  };

  protected readonly afterCancelLabel = computed(() => {
    const count = this.afterCancelEvents();
    if (count === null) return null;
    if (count <= 0) return this.copy.afterCancelNone;
    if (count === 1) {
      return $localize`:@@runs.detail.timeline.after-cancel-one:1 event arrived after cancel (informational)`;
    }
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
    return status === 'reached' && this.stateHistory() !== null && state !== 'created'
      ? 'success'
      : 'neutral';
  }
}
