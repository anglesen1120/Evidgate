import type { RunStateDto, RunStateTransition } from '@evidgate/sdk';
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

/** A side exit cannot establish which intermediate steps were served. */
const TERMINAL_STATUSES: readonly TimelineStepStatus[] = [
  'reached',
  'unrecorded',
  'unrecorded',
  'unrecorded',
  'not-reached',
  'current',
];

const LEGACY_CASES: readonly MainPathCase[] = [
  ...MAIN_PATH_CASES,
  { state: 'incomplete', expected: TERMINAL_STATUSES },
  { state: 'canceled', expected: TERMINAL_STATUSES },
];

const states = (state: RunState) => runTimelineSteps(state).map((step) => step.state);
const statuses = (state: RunState) => runTimelineSteps(state).map((step) => step.status);

const transition = (
  from_state: RunState,
  to_state: RunState,
  occurred_at: string,
): RunStateTransition => ({ from_state, to_state, occurred_at });

const FULL_HISTORY: RunStateTransition[] = [
  transition('queued', 'running', '2026-11-07T10:00:00Z'),
  transition('running', 'collecting', '2026-11-07T10:01:00Z'),
  transition('collecting', 'complete', '2026-11-07T10:02:00Z'),
];

describe('runTimelineSteps', () => {
  it.each(MAIN_PATH_CASES)(
    'runTimelineSteps_$state_marksReachedCurrentAndUpcomingAlongTheMainPath',
    ({ state, expected }) => {
      expect(states(state)).toEqual(MAIN_PATH);
      expect(statuses(state)).toEqual(expected);
    },
  );

  it('runTimelineSteps_incomplete_keepsUnservedIntermediateStepsUnrecorded', () => {
    expect(states('incomplete')).toEqual([...MAIN_PATH, 'incomplete']);
    expect(statuses('incomplete')).toEqual(TERMINAL_STATUSES);
  });

  it('runTimelineSteps_incomplete_neverMarksCompleteReachedOrCurrent', () => {
    const steps = runTimelineSteps('incomplete');
    expect(steps.find((step) => step.state === 'complete')?.status).toBe('not-reached');
    expect(steps.filter((step) => step.status === 'reached')).toEqual([
      { state: 'created', status: 'reached', at: null },
    ]);
  });

  it('runTimelineSteps_canceled_keepsUnservedIntermediateStepsUnrecorded', () => {
    expect(states('canceled')).toEqual([...MAIN_PATH, 'canceled']);
    expect(statuses('canceled')).toEqual(TERMINAL_STATUSES);
  });

  it('runTimelineSteps_canceled_neverMarksCompleteReachedOrCurrent', () => {
    const steps = runTimelineSteps('canceled');
    expect(steps.find((step) => step.state === 'complete')?.status).toBe('not-reached');
    expect(steps.filter((step) => step.status === 'reached')).toEqual([
      { state: 'created', status: 'reached', at: null },
    ]);
  });

  it.each(ALL_STATES.map((state) => ({ state })))(
    'runTimelineSteps_$state_hasOneCurrentStepAtItsOwnState',
    ({ state }) => {
      const current = runTimelineSteps(state).filter((step) => step.status === 'current');
      expect(current).toEqual([{ state, status: 'current', at: null }]);
    },
  );

  it.each([{ state: 'incomplete' as const }, { state: 'canceled' as const }])(
    'runTimelineSteps_$state_appendsItsTerminalStepLast',
    ({ state }) => {
      expect(states(state)).toHaveLength(MAIN_PATH.length + 1);
      expect(states(state).at(-1)).toBe(state);
    },
  );

  it('runTimelineSteps_fullHistory_reachesOnlyRecordedStepsWithArrivalTimes', () => {
    expect(runTimelineSteps('complete', FULL_HISTORY)).toEqual([
      { state: 'created', status: 'reached', at: null },
      { state: 'queued', status: 'reached', at: null },
      { state: 'running', status: 'reached', at: '2026-11-07T10:00:00Z' },
      { state: 'collecting', status: 'reached', at: '2026-11-07T10:01:00Z' },
      { state: 'complete', status: 'current', at: '2026-11-07T10:02:00Z' },
    ]);
  });

  it('runTimelineSteps_partialHistory_keepsLaterStepsUpcomingWithoutTimes', () => {
    const history = [transition('queued', 'running', '2026-11-07T10:00:00Z')];
    expect(runTimelineSteps('running', history)).toEqual([
      { state: 'created', status: 'reached', at: null },
      { state: 'queued', status: 'reached', at: null },
      { state: 'running', status: 'current', at: '2026-11-07T10:00:00Z' },
      { state: 'collecting', status: 'upcoming', at: null },
      { state: 'complete', status: 'upcoming', at: null },
    ]);
  });

  it('runTimelineSteps_emptyHistory_keepsOnlyCreatedCurrentAndInventsNoTimes', () => {
    const steps = runTimelineSteps('created', []);
    expect(steps.map((step) => step.status)).toEqual(MAIN_PATH_CASES[0]?.expected);
    expect(steps.every((step) => step.at === null)).toBe(true);
  });

  it.each([null, undefined])(
    'runTimelineSteps_legacyHistory_%s_preservesStatusesWithoutTimes',
    (history) => {
      for (const { state, expected } of LEGACY_CASES) {
        const steps = runTimelineSteps(state, history);
        expect(steps.map((step) => step.status)).toEqual(expected);
        expect(steps.every((step) => step.at === null)).toBe(true);
      }
    },
  );

  it.each(['canceled', 'incomplete'] as const)(
    'runTimelineSteps_%sWithLateTransitions_neverReachesComplete',
    (state) => {
      const history = [
        transition('queued', state, '2026-11-07T10:00:00Z'),
        transition('running', 'complete', '2026-11-07T10:02:00Z'),
      ];
      const steps = runTimelineSteps(state, history);
      expect(steps.find((step) => step.state === 'complete')).toEqual({
        state: 'complete',
        status: 'not-reached',
        at: null,
      });
      expect(steps.at(-1)).toEqual({ state, status: 'current', at: '2026-11-07T10:00:00Z' });
      expect(steps.find((step) => step.state === 'running')?.status).toBe('reached');
      expect(steps.find((step) => step.state === 'collecting')?.status).toBe('not-reached');
      expect(steps.some((step) => step.status === 'unrecorded')).toBe(false);
    },
  );

  it('runTimelineSteps_duplicateTransitions_usesTheEarliestArrival', () => {
    const history = [
      transition('queued', 'running', '2026-11-07T10:02:00Z'),
      transition('queued', 'running', '2026-11-07T10:00:00Z'),
      transition('queued', 'running', '2026-11-07T10:01:00Z'),
    ];
    expect(runTimelineSteps('running', history).find((step) => step.state === 'running')?.at).toBe(
      '2026-11-07T10:00:00Z',
    );
  });

  it('runTimelineSteps_outOfOrderHistory_sortsTimesWithoutMutatingTheInput', () => {
    const history = [...FULL_HISTORY].reverse();
    const original = [...history];
    expect(runTimelineSteps('complete', history)).toEqual(
      runTimelineSteps('complete', FULL_HISTORY),
    );
    expect(history).toEqual(original);
  });

  it('runTimelineSteps_equalInstants_keepsInputOrder', () => {
    const history = [
      transition('queued', 'running', '2026-11-07T11:00:00+01:00'),
      transition('queued', 'running', '2026-11-07T10:00:00Z'),
    ];
    expect(runTimelineSteps('running', history).find((step) => step.state === 'running')?.at).toBe(
      '2026-11-07T11:00:00+01:00',
    );
  });

  it('runTimelineSteps_disagreeingLastTransition_keepsRunStateAuthoritative', () => {
    const steps = runTimelineSteps('running', FULL_HISTORY);
    expect(steps.filter((step) => step.status === 'current')).toEqual([
      { state: 'running', status: 'current', at: '2026-11-07T10:00:00Z' },
    ]);
    expect(steps.find((step) => step.state === 'collecting')).toEqual({
      state: 'collecting',
      status: 'upcoming',
      at: null,
    });
    expect(steps.find((step) => step.state === 'complete')?.status).toBe('upcoming');
  });

  it('runTimelineSteps_skippedState_marksItNotReachedRatherThanInventingProgress', () => {
    const history = [
      transition('created', 'running', '2026-11-07T10:00:00Z'),
      transition('running', 'collecting', '2026-11-07T10:01:00Z'),
    ];
    expect(runTimelineSteps('collecting', history).map((step) => step.status)).toEqual([
      'reached',
      'not-reached',
      'reached',
      'current',
      'upcoming',
    ]);
  });

  it('runTimelineSteps_unparsableTime_retainsStateEvidenceWithoutAnInvalidTime', () => {
    const history: RunStateDto['state_history'] = [
      transition('queued', 'running', 'not-a-date'),
      transition('running', 'collecting', '2026-11-07T10:01:00Z'),
    ];
    expect(
      runTimelineSteps('collecting', history).find((step) => step.state === 'running'),
    ).toEqual({ state: 'running', status: 'reached', at: null });
    expect(history[0]?.occurred_at).toBe('not-a-date');
  });

  it('runTimelineSteps_mixedValidAndInvalidDuplicates_keepsTheEarliestValidTime', () => {
    const history = [
      transition('queued', 'running', '2026-11-07T10:02:00Z'),
      transition('queued', 'running', 'not-a-date'),
      transition('queued', 'running', '2026-11-07T10:00:00Z'),
    ];
    expect(runTimelineSteps('running', history).find((step) => step.state === 'running')?.at).toBe(
      '2026-11-07T10:00:00Z',
    );
  });

  it.each(['1', '2026', 'Nov 7 2026'])(
    'runTimelineSteps_lenientTime_%s_isNotAnInstantAndDoesNotDisplaceValidOrdering',
    (occurred_at) => {
      const history = [
        transition('queued', 'running', '2026-11-07T10:02:00Z'),
        transition('running', 'collecting', occurred_at),
        transition('collecting', 'complete', '2026-11-07T10:00:00Z'),
      ];
      expect(runTimelineSteps('complete', history)).toEqual([
        { state: 'created', status: 'reached', at: null },
        { state: 'queued', status: 'reached', at: null },
        { state: 'running', status: 'reached', at: '2026-11-07T10:02:00Z' },
        { state: 'collecting', status: 'reached', at: null },
        { state: 'complete', status: 'current', at: '2026-11-07T10:00:00Z' },
      ]);
    },
  );

  it('runTimelineSteps_offsetInstant_isAcceptedAndKeepsItsWrittenForm', () => {
    const history = [transition('queued', 'running', '2026-11-07T10:00:00+07:00')];
    expect(runTimelineSteps('running', history).find((step) => step.state === 'running')).toEqual({
      state: 'running',
      status: 'current',
      at: '2026-11-07T10:00:00+07:00',
    });
  });
});
