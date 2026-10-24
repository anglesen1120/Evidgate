// State sheet: docs/design/web/state-sheet.md#403-pattern
import { provideZonelessChangeDetection, reflectComponentType } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { ForbiddenPatternComponent } from './forbidden-pattern.component';

describe('ForbiddenPatternComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ForbiddenPatternComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('has no resource inputs and politely announces fixed authorized-scope copy', () => {
    const fixture = TestBed.createComponent(ForbiddenPatternComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(reflectComponentType(ForbiddenPatternComponent)?.inputs).toEqual([]);
    expect(root.querySelector('[role="status"] h2')?.textContent).toBe('Not authorized');
    expect(root.textContent).toContain('Ask a project administrator for access to this workspace.');
    expect(root.textContent).not.toMatch(/tenant|organization|exists|\bid\b/i);
    expect(
      root.querySelector('input, button, a, [tabindex], [role="button"], [aria-disabled]'),
    ).toBeNull();
    expect(root.querySelector('[role="alert"], [aria-busy], [class*="success"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(PASS|FAIL)\b/);
  });
});
