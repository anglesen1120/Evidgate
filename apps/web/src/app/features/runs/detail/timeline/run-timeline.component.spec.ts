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

  async function render(state: State, afterCancelEvents: number | null = null) {
    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('afterCancelEvents', afterCancelEvents);
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

  it('ends an incomplete run at its own terminal step and never reaches complete', async () => {
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

  it.each(ALL_STATES.filter((state) => state !== 'canceled').map((state) => ({ state })))(
    'shows no after-cancel note for a $state run',
    async ({ state }) => {
      const { root } = await render(state, 3);
      expect(root.querySelector('[data-after-cancel-note]')).toBeNull();
    },
  );

  it('states that step times are unavailable through the shared status pill', async () => {
    const { root } = await render('collecting');
    const line = root.querySelector('[data-step-times]');
    expect(line?.querySelector('app-status-pill')?.textContent).toBe(
      'Step times are not available yet',
    );
    expect(root.querySelector('time')).toBeNull();
  });
});
