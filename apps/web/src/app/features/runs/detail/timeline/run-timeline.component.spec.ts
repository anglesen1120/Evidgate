import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunStateDto } from '@evidgate/sdk';
import { RunTimelineComponent } from './run-timeline.component';

type State = RunStateDto['state'];

const ALL_STATES: readonly State[] = [
  'created',
  'queued',
  'running',
  'collecting',
  'complete',
  'incomplete',
  'canceled',
];

class TimelineHarness extends ComponentHarness {
  static hostSelector = 'app-run-timeline';
  readonly milestones = this.locatorForAll('ol > li');
  readonly deltas = this.locatorForAll('[data-step-delta]');
  readonly statusIcons = this.locatorForAll('[data-step-status] hlm-icon');
  readonly section = this.locatorFor('section');
  readonly list = this.locatorFor('ol');
  readonly heading = this.locatorFor('#run-timeline-title');
  readonly current = this.locatorForAll('[aria-current="step"]');
  readonly times = this.locatorForAll('time');
  readonly historyNote = this.locatorForOptional('[data-step-times]');
  readonly cancelNote = this.locatorForOptional('[data-after-cancel-note]');
  readonly cancelCount = this.locatorFor('[data-after-cancel-count]');
  readonly cancelCountPill = this.locatorForOptional('[data-after-cancel-count] app-status-pill');
  readonly alert = this.locatorForOptional('[role="alert"]');

  async states() {
    return Promise.all((await this.milestones()).map((step) => step.getAttribute('data-step')));
  }

  async status(state: State) {
    return (await this.locatorFor(`[data-step="${state}"] [data-step-status]`)()).text();
  }

  time(state: State) {
    return this.locatorForOptional(`[data-step="${state}"] time`)();
  }

  milestone(state: State) {
    return this.locatorFor(`[data-step="${state}"]`)();
  }
}

describe('RunTimelineComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(
    state: State,
    afterCancelEvents: number | null = null,
    stateHistory: RunStateDto['state_history'] = null,
  ) {
    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('afterCancelEvents', afterCancelEvents);
    fixture.componentRef.setInput('stateHistory', stateHistory);
    await fixture.whenStable();
    const timeline = await TestbedHarnessEnvironment.harnessForFixture(fixture, TimelineHarness);
    return { fixture, timeline };
  }

  it('renders a named ordered list with one current step and visible status text', async () => {
    const { timeline } = await render('running');
    expect(await (await timeline.section()).getAttribute('aria-labelledby')).toBe(
      'run-timeline-title',
    );
    expect(await (await timeline.list()).getAttribute('aria-labelledby')).toBe(
      'run-timeline-title',
    );
    expect(await (await timeline.heading()).text()).toBe('Run state timeline');
    expect(await timeline.states()).toEqual(['running', 'collecting', 'complete']);
    expect(await timeline.current()).toHaveLength(1);
    expect(await timeline.status('running')).toBe('Current step');
    expect(await timeline.status('collecting')).toBe('Not reached yet');
    expect(await timeline.status('complete')).toBe('Not reached yet');
  });

  it('keeps milestone labels, status icons and UTC times together in reading order', async () => {
    const { timeline } = await render('running', null, [
      { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
    ]);
    expect(await timeline.states()).toEqual(['queued', 'running', 'collecting', 'complete']);
    expect(await timeline.statusIcons()).toHaveLength(4);
    const running = await timeline.milestone('running');
    expect(await running.text()).toMatch(/Running.*Current step.*UTC/s);
    expect(await (await timeline.milestone('queued')).text()).toContain('Time unavailable');
  });

  it('shows elapsed time between recorded transitions, including a real zero and a side exit', async () => {
    const { timeline } = await render('canceled', null, [
      { from_state: 'created', to_state: 'queued', occurred_at: '2026-11-07T11:00:00+01:00' },
      { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
      { from_state: 'running', to_state: 'canceled', occurred_at: '2026-11-07T10:00:02.250Z' },
    ]);
    expect(await timeline.deltas()).toHaveLength(2);
    expect(await (await timeline.milestone('running')).text()).toContain('+0 ms');
    expect(await (await timeline.milestone('canceled')).text()).toContain('+2.25 s');
  });

  it('never bridges an undated transition or reports a negative duration', async () => {
    const { timeline } = await render('complete', null, [
      { from_state: 'created', to_state: 'queued', occurred_at: '2026-11-07T10:00:00Z' },
      { from_state: 'queued', to_state: 'running', occurred_at: 'not-a-date' },
      { from_state: 'running', to_state: 'collecting', occurred_at: '2026-11-07T10:01:00Z' },
      { from_state: 'collecting', to_state: 'complete', occurred_at: '2026-11-07T10:00:59Z' },
    ]);
    expect(await timeline.deltas()).toHaveLength(0);
    expect(await (await timeline.milestone('running')).text()).toContain('Time unavailable');
  });

  it.each(ALL_STATES.map((state) => ({ state })))(
    'marks exactly the $state step as current',
    async ({ state }) => {
      const { timeline } = await render(state);
      const current = await timeline.current();
      expect(current).toHaveLength(1);
      expect(await current[0]?.getAttribute('data-step')).toBe(state);
      expect(await timeline.status(state)).toBe('Current step');
    },
  );

  it('omits inferred historical steps instead of presenting them as recorded facts', async () => {
    const { timeline } = await render('collecting');
    expect(await timeline.states()).toEqual(['collecting', 'complete']);
    expect(await timeline.status('complete')).toBe('Not reached yet');
  });

  it('ends an incomplete run at its own terminal step, never reaching complete', async () => {
    const { timeline } = await render('incomplete');
    expect(await timeline.states()).toEqual([
      'queued',
      'running',
      'collecting',
      'complete',
      'incomplete',
    ]);
    // The deadline can close the run straight from queued or running, so no intermediate step is
    // claimed as served.
    for (const state of ['queued', 'running', 'collecting'] as const) {
      expect(await timeline.status(state)).toBe('Not recorded');
    }
    expect(await timeline.status('incomplete')).toBe('Current step');
    expect(await timeline.status('complete')).toBe('Not reached');
  });

  it('never marks a canceled run complete, even when late events arrived', async () => {
    const { timeline } = await render('canceled', 3);
    const complete = await timeline.milestone('complete');
    expect(await complete.getAttribute('data-status')).toBe('not-reached');
    expect(await timeline.status('complete')).toBe('Not reached');
    expect(await (await timeline.milestone('canceled')).getAttribute('aria-current')).toBe('step');
    expect(await timeline.states()).not.toContain('created');
    expect(await timeline.status('running')).toBe('Not recorded');
  });

  it('keeps a canceled run informational and names the after-cancel event count', async () => {
    const { timeline } = await render('canceled', 3);
    const note = await timeline.cancelNote();
    expect(await note?.text()).toContain(
      'Events that arrive after cancel are kept as evidence and never complete the run.',
    );
    expect(await (await timeline.cancelCount()).text()).toContain(
      '3 events arrived after cancel (informational)',
    );
    expect(await note?.getAttribute('role')).toBe('note');
    expect(await timeline.alert()).toBeNull();
    const classes = await note?.getAttribute('class');
    expect(classes).toContain('eg-info');
    expect(classes).not.toContain('eg-warning');
    expect(classes).not.toContain('eg-danger');
  });

  it('shows the unavailable state for an unserved after-cancel count', async () => {
    const { timeline } = await render('canceled', null);
    const count = await (await timeline.cancelCount()).text();
    expect(count).toContain('After-cancel events');
    expect(count).toContain('Not provided');
    expect(await timeline.cancelCountPill()).toBeNull();
    expect(count).not.toMatch(/\d+ events/);
  });

  it('says no events arrived after cancel when the count is zero', async () => {
    const { timeline } = await render('canceled', 0);
    expect(await (await timeline.cancelCount()).text()).toBe('No events after cancel');
  });

  it('uses singular copy for exactly one after-cancel event', async () => {
    const { timeline } = await render('canceled', 1);
    expect(await (await timeline.cancelCount()).text()).toContain(
      '1 event arrived after cancel (informational)',
    );
  });

  it.each(ALL_STATES.filter((state) => state !== 'canceled').map((state) => ({ state })))(
    'shows no after-cancel note for a $state run',
    async ({ state }) => {
      const { timeline } = await render(state, 3);
      expect(await timeline.cancelNote()).toBeNull();
    },
  );

  it('shows unknown history copy without inventing any step times', async () => {
    const { timeline } = await render('collecting');
    expect(await (await timeline.historyNote())?.text()).toBe('History unavailable for this run');
    expect(await timeline.times()).toHaveLength(0);
    expect(await timeline.deltas()).toHaveLength(0);
  });

  it('distinguishes a known empty history from unavailable history', async () => {
    const { timeline } = await render('created', null, []);
    expect(await (await timeline.historyNote())?.text()).toBe('No state changes recorded yet');
    expect(await (await timeline.milestone('created')).getAttribute('aria-current')).toBe('step');
    expect(await timeline.times()).toHaveLength(0);
    expect(await timeline.deltas()).toHaveLength(0);
  });

  it('renders served arrival instants as formatted semantic times without a history note', async () => {
    const history: RunStateDto['state_history'] = [
      { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
      { from_state: 'running', to_state: 'collecting', occurred_at: '2026-11-07T10:01:00Z' },
      { from_state: 'collecting', to_state: 'complete', occurred_at: '2026-11-07T10:02:00Z' },
    ];
    const { timeline } = await render('complete', null, history);
    const times = await timeline.times();
    expect(times).toHaveLength(3);
    expect(await Promise.all(times.map((time) => time.getAttribute('datetime')))).toEqual(
      history.map((transition) => transition.occurred_at),
    );
    const labels = await Promise.all(times.map((time) => time.text()));
    expect(labels.map((label) => label.replace(/\s+/g, ' ').trim())).toEqual([
      '11/7/2026, 10:00:00 AM UTC',
      '11/7/2026, 10:01:00 AM UTC',
      '11/7/2026, 10:02:00 AM UTC',
    ]);
    expect(await timeline.time('queued')).toBeNull();
    expect(await timeline.historyNote()).toBeNull();
    expect(await timeline.status('queued')).toBe('Reached');
    expect(await timeline.states()).not.toContain('created');
    expect(await timeline.status('running')).toBe('Reached');
  });

  it('preserves after-cancel information while suppressing late completion times', async () => {
    const history: RunStateDto['state_history'] = [
      { from_state: 'queued', to_state: 'canceled', occurred_at: '2026-11-07T10:00:00Z' },
      { from_state: 'running', to_state: 'complete', occurred_at: '2026-11-07T10:01:00Z' },
    ];
    const { timeline } = await render('canceled', 3, history);
    expect(await (await timeline.milestone('complete')).getAttribute('data-status')).toBe(
      'not-reached',
    );
    expect(await timeline.time('complete')).toBeNull();
    expect(await (await timeline.time('canceled'))?.getAttribute('datetime')).toBe(
      '2026-11-07T10:00:00Z',
    );
    expect(await (await timeline.cancelNote())?.text()).toContain(
      'Events that arrive after cancel are kept as evidence and never complete the run.',
    );
    expect(await (await timeline.cancelCount()).text()).toContain(
      '3 events arrived after cancel (informational)',
    );
  });

  it('keeps malformed history timestamps from breaking the timeline', async () => {
    const { timeline } = await render('running', null, [
      { from_state: 'queued', to_state: 'running', occurred_at: 'not-a-date' },
    ]);
    expect(await (await timeline.milestone('running')).getAttribute('aria-current')).toBe('step');
    expect(await timeline.times()).toHaveLength(0);
    expect(await timeline.historyNote()).toBeNull();
  });

  it('updates derived step times and notes when the served history changes', async () => {
    const { timeline, fixture } = await render('running');
    fixture.componentRef.setInput('stateHistory', [
      { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
    ]);
    await fixture.whenStable();
    expect(await (await timeline.time('running'))?.getAttribute('datetime')).toBe(
      '2026-11-07T10:00:00Z',
    );
    expect(await timeline.historyNote()).toBeNull();

    fixture.componentRef.setInput('stateHistory', null);
    await fixture.whenStable();
    expect(await timeline.times()).toHaveLength(0);
    expect(await (await timeline.historyNote())?.text()).toBe('History unavailable for this run');
  });
});
