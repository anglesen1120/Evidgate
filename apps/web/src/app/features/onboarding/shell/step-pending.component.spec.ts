import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { OnboardingProgress } from '../data/onboarding-progress.service';
import { PROJECT, runState } from '../onboarding.store.test-support';

import { StepPendingComponent } from './step-pending.component';

describe('StepPendingComponent', () => {
  it('renders one neutral localized panel without a mutation or invented verdict', async () => {
    await TestBed.configureTestingModule({
      imports: [StepPendingComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(StepPendingComponent);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-testid="onboarding-step-pending"]')).toHaveLength(1);
    expect(root.textContent).toContain('This step is not available yet');
    expect(root.textContent).toContain('Viewing a step does not complete it.');
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b|enabled and collecting/i);
    expect(root.querySelector('button, input, [role="button"]')).toBeNull();
    const panel = root.querySelector('[data-testid="onboarding-step-pending"]');
    expect(panel?.getAttribute('data-slot')).toBe('alert');
    expect(panel?.getAttribute('role')).toBe('note');
    expect(root.querySelector('[data-slot="alert-title"]')?.textContent).toContain(
      'not available yet',
    );
    expect(root.querySelector('[tabindex]')).toBeNull();
  });

  async function recorded(run = runState('run-1', 'evidence-1'), step = 3) {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [StepPendingComponent],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(StepPendingComponent);
    fixture.componentRef.setInput('orgId', PROJECT.org_id);
    fixture.componentRef.setInput('step', step);
    fixture.componentRef.setInput('progress', {
      project: PROJECT,
      sampleRun: run,
      activation: { kind: 'none' },
      bindings: [],
      tokens: [],
      bindingsTruncated: false,
      tokensTruncated: false,
      done: { 1: true, 2: true, 3: true, 4: true, 5: false },
      currentStep: 5,
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it.each([3, 4])('shows server records and a scoped Runs link for step %s', async (step) => {
    const { root } = await recorded(runState('run-1', 'evidence-1'), step);
    expect(root.textContent).toContain('Recorded setup progress');
    expect(root.textContent).toContain('run-1');
    expect(root.textContent).toContain('evidence-1');
    expect(root.textContent).not.toContain('This step is not available yet');
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/orgs/o-1/projects/p-1/runs');
    expect(root.querySelector('a')?.textContent?.trim()).toBe('Open Runs');
    expect(root.querySelector('button, input')).toBeNull();
    if (step === 4) expect(root.textContent).toContain('does not display a report comparison');
    else expect(root.textContent).toContain('does not run CI');
  });

  it('does not invent an evidence revision for a run still collecting', async () => {
    const { root } = await recorded(runState('run-1', null), 4);
    expect(root.textContent).toContain('No evidence revision is recorded');
    expect(root.textContent).toContain('run-1');
    expect(root.textContent).not.toContain('Evidence revision ID');
    expect(root.querySelector('a')).not.toBeNull();
  });

  it('gives a useful next action without a Runs link when no run was recorded', async () => {
    const { fixture, root } = await recorded();
    fixture.componentRef.setInput('progress', {
      project: PROJECT,
      sampleRun: null,
      activation: { kind: 'none' },
      bindings: [],
      tokens: [],
      bindingsTruncated: false,
      tokensTruncated: false,
      done: { 1: true, 2: true, 3: false, 4: false, 5: false },
      currentStep: 3,
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    expect(root.textContent).toContain('No run is recorded for this project yet');
    expect(root.textContent).toContain('Run your connected CI');
    expect(root.querySelector('a, code')).toBeNull();
    fixture.componentRef.setInput('progress', null);
    await fixture.whenStable();
    expect(root.textContent).toContain('Viewing a step does not complete it.');
    expect(root.querySelector('a')).toBeNull();
  });
});
