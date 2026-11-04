import { TestBed } from '@angular/core/testing';
import type { AttemptRow } from './attempt-summary';
import { AttemptsTableComponent } from './attempts-table.component';

export function row(overrides: Partial<AttemptRow> = {}): AttemptRow {
  return {
    executionKey: 'checkout',
    attemptNo: 1,
    rawStatus: 'provider-failure',
    normalisedStatus: 'failed',
    durationMs: 320,
    artifactCount: 2,
    ...overrides,
  };
}

export async function render(attempts: AttemptRow[] | null) {
  const fixture = TestBed.createComponent(AttemptsTableComponent);
  fixture.componentRef.setInput('attempts', attempts);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

export function rows(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>('[data-attempt-row]')];
}

export function requiredElement<T extends HTMLElement>(root: HTMLElement, selector: string): T {
  const element = root.querySelector<T>(selector);
  if (!element) throw new Error(`Missing test element: ${selector}`);
  return element;
}
