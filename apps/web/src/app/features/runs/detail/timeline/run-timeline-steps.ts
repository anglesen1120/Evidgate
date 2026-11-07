import type { RunStateDto, RunStateTransition } from '@evidgate/sdk';
import type { RunState } from '../../../../shared/ui/chips/run-state-tone';

/** How a step of the run-state timeline stands relative to the run's current state. */
export type TimelineStepStatus = 'reached' | 'current' | 'upcoming' | 'not-reached' | 'unrecorded';

export interface TimelineStep {
  readonly state: RunState;
  readonly status: TimelineStepStatus;
  readonly at: string | null;
}

/** The collection lifecycle's main path, in order. Terminal side exits are appended after it. */
const MAIN_PATH: readonly RunState[] = ['created', 'queued', 'running', 'collecting', 'complete'];

function mainPathStatuses(currentIndex: number): readonly TimelineStep[] {
  return MAIN_PATH.map((state, position): TimelineStep => ({
    state,
    status:
      position < currentIndex ? 'reached' : position === currentIndex ? 'current' : 'upcoming',
    at: null,
  }));
}

/** Unknown legacy history cannot establish which intermediate steps preceded a side exit. */
function legacySteps(state: RunState, currentIndex: number): readonly TimelineStep[] {
  if (currentIndex >= 0) return mainPathStatuses(currentIndex);
  const terminal: TimelineStep = { state, status: 'current', at: null };
  const unknownProgress = MAIN_PATH.map((step): TimelineStep => ({
    state: step,
    status: step === 'created' ? 'reached' : step === 'complete' ? 'not-reached' : 'unrecorded',
    at: null,
  }));
  return [...unknownProgress, terminal];
}

function orderedHistory(history: RunStateTransition[]): RunStateTransition[] {
  const timed = history
    .filter((transition) => Number.isFinite(Date.parse(transition.occurred_at)))
    .sort(
      (left: RunStateTransition, right: RunStateTransition) =>
        Date.parse(left.occurred_at) - Date.parse(right.occurred_at),
    );
  let index = 0;
  // Malformed times retain their input slots without preventing valid entries from sorting.
  return history.map((transition) =>
    Number.isFinite(Date.parse(transition.occurred_at))
      ? (timed[index++] ?? transition)
      : transition,
  );
}

function knownStatus(
  state: RunState,
  position: number,
  currentIndex: number,
  reached: ReadonlySet<RunState>,
): TimelineStepStatus {
  if (position === currentIndex) return 'current';
  if (currentIndex < 0 && state === 'complete') return 'not-reached';
  if (currentIndex >= 0 && position > currentIndex) return 'upcoming';
  return reached.has(state) ? 'reached' : 'not-reached';
}

/** Served history establishes reached steps and times; the run state remains authoritative. */
export function runTimelineSteps(
  state: RunState,
  history: RunStateDto['state_history'] | undefined = null,
): readonly TimelineStep[] {
  const currentIndex = MAIN_PATH.indexOf(state);
  if (history === null || history === undefined) return legacySteps(state, currentIndex);

  const reached = new Set<RunState>(['created']);
  const arrivals = new Map<RunState, string>();
  for (const transition of orderedHistory(history)) {
    reached.add(transition.from_state);
    reached.add(transition.to_state);
    if (!arrivals.has(transition.to_state) && Number.isFinite(Date.parse(transition.occurred_at))) {
      arrivals.set(transition.to_state, transition.occurred_at);
    }
  }
  const steps = MAIN_PATH.map((step, position): TimelineStep => {
    const status = knownStatus(step, position, currentIndex, reached);
    return {
      state: step,
      status,
      at:
        step !== 'created' && (status === 'reached' || status === 'current')
          ? (arrivals.get(step) ?? null)
          : null,
    };
  });
  return currentIndex >= 0
    ? steps
    : [...steps, { state, status: 'current', at: arrivals.get(state) ?? null }];
}
