import type { RunState } from '../../../../shared/ui/chips/run-state-tone';
import { runTimelineSteps, type TimelineStepStatus } from './run-timeline-steps';

const MAIN_PATH: readonly RunState[] = ['created', 'queued', 'running', 'collecting', 'complete'];

const ALL_STATES: readonly RunState[] = [...MAIN_PATH, 'incomplete', 'canceled'];

interface MainPathCase {
  readonly state: RunState;
  readonly expected: readonly TimelineStepStatus[];
}

const MAIN_PATH_CASES: readonly MainPathCase[] = [
  { state: 'created', expected: ['current', 'upcoming', 'upcoming', 'upcoming', 'upcoming'] },
  { state: 'queued', expected: ['reached', 'current', 'upcoming', 'upcoming', 'upcoming'] },
  { state: 'running', expected: ['reached', 'reached', 'current', 'upcoming', 'upcoming'] },
  { state: 'collecting', expected: ['reached', 'reached', 'reached', 'current', 'upcoming'] },
  { state: 'complete', expected: ['reached', 'reached', 'reached', 'reached', 'current'] },
];

const states = (state: RunState) => runTimelineSteps(state).map((step) => step.state);
const statuses = (state: RunState) => runTimelineSteps(state).map((step) => step.status);

describe('runTimelineSteps', () => {
  it.each(MAIN_PATH_CASES)(
    'runTimelineSteps_$state_marksReachedCurrentAndUpcomingAlongTheMainPath',
    ({ state, expected }) => {
      expect(states(state)).toEqual(MAIN_PATH);
      expect(statuses(state)).toEqual(expected);
    },
  );

  it('runTimelineSteps_incomplete_endsAtIncompleteAndNeverReachesComplete', () => {
    expect(states('incomplete')).toEqual([...MAIN_PATH, 'incomplete']);
    expect(statuses('incomplete')).toEqual([
      'reached',
      'reached',
      'reached',
      'reached',
      'not-reached',
      'current',
    ]);
  });

  it('runTimelineSteps_canceled_keepsUnservedIntermediateStepsUnrecorded', () => {
    expect(states('canceled')).toEqual([...MAIN_PATH, 'canceled']);
    expect(statuses('canceled')).toEqual([
      'reached',
      'unrecorded',
      'unrecorded',
      'unrecorded',
      'not-reached',
      'current',
    ]);
  });

  it('runTimelineSteps_canceled_neverMarksCompleteReachedOrCurrent', () => {
    const steps = runTimelineSteps('canceled');
    expect(steps.find((step) => step.state === 'complete')?.status).toBe('not-reached');
    expect(steps.filter((step) => step.status === 'reached')).toEqual([
      { state: 'created', status: 'reached' },
    ]);
  });

  it.each(ALL_STATES.map((state) => ({ state })))(
    'runTimelineSteps_$state_hasOneCurrentStepAtItsOwnState',
    ({ state }) => {
      const current = runTimelineSteps(state).filter((step) => step.status === 'current');
      expect(current).toEqual([{ state, status: 'current' }]);
    },
  );

  it.each([{ state: 'incomplete' as const }, { state: 'canceled' as const }])(
    'runTimelineSteps_$state_appendsItsTerminalStepLast',
    ({ state }) => {
      expect(states(state)).toHaveLength(MAIN_PATH.length + 1);
      expect(states(state).at(-1)).toBe(state);
    },
  );
});
