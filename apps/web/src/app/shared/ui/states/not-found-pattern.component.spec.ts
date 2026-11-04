// State sheet: docs/design/web/state-sheet.md#404-pattern
import { provideZonelessChangeDetection, reflectComponentType } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { NotFoundPatternComponent } from './not-found-pattern.component';

describe('NotFoundPatternComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NotFoundPatternComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('has no resource inputs and politely announces an existence-agnostic explanation', () => {
    const fixture = TestBed.createComponent(NotFoundPatternComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(reflectComponentType(NotFoundPatternComponent)?.inputs).toEqual([]);
    expect(root.querySelector('section')?.getAttribute('data-slot')).toBe('card');
    expect(root.querySelector('h2')?.getAttribute('data-slot')).toBe('card-title');
    expect(root.querySelector('[role="status"] h2')?.textContent?.trim()).toBe('Not found');
    expect(root.textContent).toContain('This page does not exist or you do not have access to it.');
    expect(root.textContent).not.toMatch(/tenant|organization|exists|\bid\b|elsewhere/i);
    expect(
      root.querySelector('input, button, a, [tabindex], [role="button"], [aria-disabled]'),
    ).toBeNull();
    expect(root.querySelector('[role="alert"], [aria-busy], [class*="success"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(PASS|FAIL)\b/);
  });
});
