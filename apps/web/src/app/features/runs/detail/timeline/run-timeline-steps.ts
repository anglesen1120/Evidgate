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

/**
 * The date-time shape `<time datetime>` and the `date` pipe render: an RFC 3339 date-time with a
 * numeric offset or `Z`. `Date.parse` alone is lenient enough to accept `'1'`, `'2026'` and
 * `'Nov 7 2026'`, which the timeline must not surface as a step time.
 */
const INSTANT_SHAPE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;

/** {@link value} in milliseconds, or `null` when it is not an instant this surface can render. */
function instantMs(value: string): number | null {
  if (!INSTANT_SHAPE.test(value)) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function orderedHistory(history: RunStateTransition[]): RunStateTransition[] {
  const entries = history.map((transition) => ({
    transition,
    atMs: instantMs(transition.occurred_at),
  }));
  const timed: { transition: RunStateTransition; atMs: number }[] = [];
  for (const entry of entries) {
    if (entry.atMs !== null) timed.push({ transition: entry.transition, atMs: entry.atMs });
  }
  timed.sort((left, right) => left.atMs - right.atMs);
  let index = 0;
  // Malformed times retain their input slots without preventing valid entries from sorting.
  return entries.map((entry) =>
    entry.atMs === null ? entry.transition : (timed[index++]?.transition ?? entry.transition),
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
    if (!arrivals.has(transition.to_state) && instantMs(transition.occurred_at) !== null) {
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
