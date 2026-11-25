import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmDialogService } from '@evidgate/ui/dialog';
import { openAttemptInspector } from './attempt-inspector.dialog';
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

export async function inspect(fixture: ComponentFixture<AttemptsTableComponent>, index = 0) {
  const host = fixture.nativeElement as HTMLElement;
  const component = fixture.componentInstance;
  const dialogs = TestBed.inject(HlmDialogService);
  const subscription = component.inspect.subscribe((attempt) =>
    openAttemptInspector(dialogs, { attempt, attempts: component.attempts() ?? [] }),
  );
  const opener = requiredElement<HTMLButtonElement>(rows(host)[index]!, 'button[appRowAction]');
  opener.click();
  await fixture.whenStable();
  subscription.unsubscribe();
  return requiredElement(document.body, '[role="dialog"]');
}

export function rows(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>('[data-row-id]')];
}

export function requiredElement<T extends HTMLElement>(root: HTMLElement, selector: string): T {
  const element = root.querySelector<T>(selector);
  if (!element) throw new Error(`Missing test element: ${selector}`);
  return element;
}
