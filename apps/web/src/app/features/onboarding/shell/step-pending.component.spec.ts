import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

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
  });
});
