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
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  const stepStatus = (root: HTMLElement, state: State) =>
    root.querySelector(`[data-step="${state}"] [data-step-status]`)?.textContent ?? '';

  it('renders a named ordered list with one current step and visible status text', async () => {
    const { root } = await render('running');
    const section = root.querySelector('section');
    expect(section?.getAttribute('aria-labelledby')).toBe('run-timeline-title');
    expect(root.querySelector('ol')?.getAttribute('aria-labelledby')).toBe('run-timeline-title');
    expect(root.querySelector('#run-timeline-title')?.textContent).toBe('Run state timeline');
    expect(root.querySelectorAll('ol > li')).toHaveLength(5);
    expect(root.querySelectorAll('[aria-current="step"]')).toHaveLength(1);
    expect(root.querySelector('[aria-current="step"]')?.getAttribute('data-step')).toBe('running');
    expect(stepStatus(root, 'created')).toContain('Reached');
    expect(stepStatus(root, 'running')).toContain('Current step');
    expect(stepStatus(root, 'collecting')).toContain('Not reached yet');
    expect(stepStatus(root, 'complete')).toContain('Not reached yet');
  });

  it.each(ALL_STATES.map((state) => ({ state })))(
    'marks exactly the $state step as current',
    async ({ state }) => {
      const { root } = await render(state);
      const current = root.querySelectorAll('[aria-current="step"]');
      expect(current).toHaveLength(1);
      expect(current[0]?.getAttribute('data-step')).toBe(state);
      expect(current[0]?.querySelector('[data-step-status]')?.textContent).toContain(
        'Current step',
      );
    },
  );

  it('marks every earlier main-path step reached for a collecting run', async () => {
    const { root } = await render('collecting');
    for (const state of ['created', 'queued', 'running'] as const) {
      expect(stepStatus(root, state)).toContain('Reached');
    }
    expect(stepStatus(root, 'complete')).toContain('Not reached yet');
  });

  it('ends an incomplete run at its own terminal step, never reaching complete', async () => {
    const { root } = await render('incomplete');
    const rows = [...root.querySelectorAll('ol > li')];
    expect(rows.map((row) => row.getAttribute('data-step'))).toEqual([
      'created',
      'queued',
      'running',
      'collecting',
      'complete',
      'incomplete',
    ]);
    expect(stepStatus(root, 'created')).toContain('Reached');
    // The deadline can close the run straight from queued or running, so no intermediate step is
    // claimed as served.
    for (const state of ['queued', 'running', 'collecting'] as const) {
      expect(stepStatus(root, state)).toContain('Not recorded');
      expect(stepStatus(root, state)).not.toContain('Reached');
    }
    expect(stepStatus(root, 'incomplete')).toContain('Current step');
    expect(stepStatus(root, 'complete')).toContain('Not reached');
    expect(stepStatus(root, 'complete')).not.toContain('Reached');
  });

  it('never marks a canceled run complete, even when late events arrived', async () => {
    const { root } = await render('canceled', 3);
    const complete = root.querySelector('[data-step="complete"]');
    expect(complete?.getAttribute('data-status')).toBe('not-reached');
    const completeStatus = stepStatus(root, 'complete');
    expect(completeStatus).toContain('Not reached');
    expect(completeStatus).not.toContain('Reached');
    expect(completeStatus).not.toContain('Complete');
    expect(root.querySelector('[aria-current="step"]')?.getAttribute('data-step')).toBe('canceled');
    expect(stepStatus(root, 'created')).toContain('Reached');
    expect(stepStatus(root, 'running')).toContain('Not recorded');
  });

  it('keeps a canceled run informational and names the after-cancel event count', async () => {
    const { root } = await render('canceled', 3);
    const note = root.querySelector('[data-after-cancel-note]');
    expect(note?.textContent).toContain(
      'Events that arrive after cancel are kept as evidence and never complete the run.',
    );
    expect(root.querySelector('[data-after-cancel-count]')?.textContent).toContain(
      '3 events arrived after cancel (informational)',
    );
    expect(note?.getAttribute('role')).toBeNull();
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(note?.className).toContain('eg-info');
    expect(note?.className).not.toContain('eg-warning');
    expect(note?.className).not.toContain('eg-danger');
  });

  it('shows the unavailable state for an unserved after-cancel count', async () => {
    const { root } = await render('canceled', null);
    const count = root.querySelector('[data-after-cancel-count]');
    expect(count?.textContent).toContain('After-cancel events');
    expect(count?.textContent).toContain('Status unavailable');
    expect(count?.textContent).not.toMatch(/\d+ events/);
  });

  it('says no events arrived after cancel when the count is zero', async () => {
    const { root } = await render('canceled', 0);
    const count = root.querySelector('[data-after-cancel-count]');
    expect(count?.textContent).toContain('No events after cancel');
    expect(count?.textContent).not.toContain('Status unavailable');
  });

  it('uses singular copy for exactly one after-cancel event', async () => {
    const { root } = await render('canceled', 1);
    expect(root.querySelector('[data-after-cancel-count]')?.textContent).toContain(
      '1 event arrived after cancel (informational)',
    );
  });

  it.each(ALL_STATES.filter((state) => state !== 'canceled').map((state) => ({ state })))(
    'shows no after-cancel note for a $state run',
    async ({ state }) => {
      const { root } = await render(state, 3);
      expect(root.querySelector('[data-after-cancel-note]')).toBeNull();
    },
  );

  it('shows unknown history copy without inventing any step times', async () => {
    const { root } = await render('collecting');
    const line = root.querySelector('[data-step-times]');
    expect(line?.querySelector('app-status-pill')?.textContent).toBe(
      'History unavailable for this run',
    );
    expect(root.querySelector('time')).toBeNull();
  });

  it('distinguishes a known empty history from unavailable history', async () => {
    const { root } = await render('created', null, []);
    expect(root.querySelector('[data-step-times]')?.textContent).toContain(
      'No state changes recorded yet',
    );
    expect(root.textContent).not.toContain('History unavailable for this run');
    expect(root.querySelector('[data-step="created"]')?.getAttribute('aria-current')).toBe('step');
    expect(root.querySelector('time')).toBeNull();
  });

  it('renders served arrival instants as formatted semantic times without a history note', async () => {
    const history: RunStateDto['state_history'] = [
      { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
      { from_state: 'running', to_state: 'collecting', occurred_at: '2026-11-07T10:01:00Z' },
      { from_state: 'collecting', to_state: 'complete', occurred_at: '2026-11-07T10:02:00Z' },
    ];
    const { root } = await render('complete', null, history);
    const times = [...root.querySelectorAll<HTMLTimeElement>('[data-step-time]')];
    expect(times).toHaveLength(3);
    expect(times.map((time) => time.tagName)).toEqual(['TIME', 'TIME', 'TIME']);
    expect(times.map((time) => time.getAttribute('datetime'))).toEqual(
      history.map((transition) => transition.occurred_at),
    );
    expect(times.map((time) => time.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'Nov 7, 2026, 10:00:00 AM',
      'Nov 7, 2026, 10:01:00 AM',
      'Nov 7, 2026, 10:02:00 AM',
    ]);
    expect(root.querySelector('[data-step="created"] time')).toBeNull();
    expect(root.querySelector('[data-step="queued"] time')).toBeNull();
    expect(root.querySelector('[data-step-times]')).toBeNull();
  });

  it('preserves after-cancel information while suppressing late completion times', async () => {
    const history: RunStateDto['state_history'] = [
      { from_state: 'queued', to_state: 'canceled', occurred_at: '2026-11-07T10:00:00Z' },
      { from_state: 'running', to_state: 'complete', occurred_at: '2026-11-07T10:01:00Z' },
    ];
    const { root } = await render('canceled', 3, history);
    expect(root.querySelector('[data-step="complete"]')?.getAttribute('data-status')).toBe(
      'not-reached',
    );
    expect(root.querySelector('[data-step="complete"] time')).toBeNull();
    expect(root.querySelector('[data-step="canceled"] time')?.getAttribute('datetime')).toBe(
      '2026-11-07T10:00:00Z',
    );
    expect(root.querySelector('[data-after-cancel-note]')?.textContent).toContain(
      'Events that arrive after cancel are kept as evidence and never complete the run.',
    );
    expect(root.querySelector('[data-after-cancel-count]')?.textContent).toContain(
      '3 events arrived after cancel (informational)',
    );
  });

  it('keeps malformed history timestamps from breaking the timeline', async () => {
    const { root } = await render('running', null, [
      { from_state: 'queued', to_state: 'running', occurred_at: 'not-a-date' },
    ]);
    expect(root.querySelector('[aria-current="step"]')?.getAttribute('data-step')).toBe('running');
    expect(root.querySelector('time')).toBeNull();
    expect(root.querySelector('[data-step-times]')).toBeNull();
  });

  it('updates derived step times and notes when the served history changes', async () => {
    const { root, fixture } = await render('running');
    fixture.componentRef.setInput('stateHistory', [
      { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
    ]);
    await fixture.whenStable();
    expect(root.querySelector('[data-step="running"] time')?.getAttribute('datetime')).toBe(
      '2026-11-07T10:00:00Z',
    );
    expect(root.querySelector('[data-step-times]')).toBeNull();

    fixture.componentRef.setInput('stateHistory', null);
    await fixture.whenStable();
    expect(root.querySelector('time')).toBeNull();
    expect(root.querySelector('[data-step-times]')?.textContent).toContain(
      'History unavailable for this run',
    );
  });
});
