import type { RunState } from '../../../../shared/ui/chips/run-state-tone';

/** How a step of the run-state timeline stands relative to the run's current state. */
export type TimelineStepStatus = 'reached' | 'current' | 'upcoming' | 'not-reached' | 'unrecorded';

export interface TimelineStep {
  readonly state: RunState;
  readonly status: TimelineStepStatus;
}

/** The collection lifecycle's main path, in order. Terminal side exits are appended after it. */
const MAIN_PATH: readonly RunState[] = ['created', 'queued', 'running', 'collecting', 'complete'];

function mainPathStatuses(currentIndex: number): readonly TimelineStep[] {
  return MAIN_PATH.map((state, position): TimelineStep => ({
    state,
    status:
      position < currentIndex ? 'reached' : position === currentIndex ? 'current' : 'upcoming',
  }));
}

/**
 * The run response carries the current state and no transition history, so a side exit cannot
 * claim the steps it passed through: an `incomplete` run did enter collecting, a `canceled` run
 * could have been canceled anywhere, and `complete` is unreachable once a run is terminal.
 */
export function runTimelineSteps(state: RunState): readonly TimelineStep[] {
  const currentIndex = MAIN_PATH.indexOf(state);
  if (currentIndex >= 0) {
    return mainPathStatuses(currentIndex);
  }
  const terminal: TimelineStep = { state, status: 'current' };
  if (state === 'incomplete') {
    const passed = MAIN_PATH.map((step): TimelineStep => ({
      state: step,
      status: step === 'complete' ? 'not-reached' : 'reached',
    }));
    return [...passed, terminal];
  }
  const unknownProgress = MAIN_PATH.map((step): TimelineStep => ({
    state: step,
    status: step === 'created' ? 'reached' : step === 'complete' ? 'not-reached' : 'unrecorded',
  }));
  return [...unknownProgress, terminal];
}
