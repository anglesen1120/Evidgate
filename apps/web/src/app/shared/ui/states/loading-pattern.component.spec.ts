// State sheet: docs/design/web/state-sheet.md#loading-pattern
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LoadingPatternComponent } from './loading-pattern.component';

const rows = [
  ['default', 'implemented — polite busy region'],
  ['hover', 'n/a — render-only panel'],
  ['focus-visible', 'n/a — render-only panel'],
  ['active', 'n/a — render-only panel'],
  ['disabled', 'n/a — render-only panel'],
  ['loading', 'implemented — polite busy region'],
  ['empty', 'n/a — route-level replacement'],
  ['error', 'n/a — route-level replacement'],
  ['success', 'n/a — route-level replacement'],
] as const;

function styles(): string {
  return Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .filter((css) => css.includes('.state-panel') || css.includes('.loading-pattern__skeleton'))
    .join('\n');
}

describe('LoadingPatternComponent', () => {
  let fixture: ComponentFixture<LoadingPatternComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LoadingPatternComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(LoadingPatternComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  for (const [state, contract] of rows) {
    describe(`${state}: ${contract}`, () => {
      it('keeps visible loading copy and never introduces an interactive or success mode', () => {
        const region = root.querySelector('[role="status"]');
        expect(region?.getAttribute('aria-live')).toBe('polite');
        expect(region?.getAttribute('aria-busy')).toBe('true');
        expect(region?.textContent).toContain('Loading evidence');
        expect(root.querySelector('.loading-pattern__skeleton')?.getAttribute('aria-hidden')).toBe(
          'true',
        );
        expect(
          root.querySelector('[tabindex], [role="button"], [aria-disabled], button, a'),
        ).toBeNull();
        expect(root.querySelector('[class*="success"], [role="alert"]')).toBeNull();
        expect(root.textContent).not.toMatch(/\b(PASS|FAIL)\b/);
      });
    });
  }

  it('renders the caller-localized label as text, including long Vietnamese copy', () => {
    const label = 'Đang tải bằng chứng '.repeat(30) + '<script>untrusted</script>';
    fixture.componentRef.setInput('label', label);
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent?.trim()).toBe(label);
    expect(root.querySelector('script')).toBeNull();
    fixture.componentRef.setInput('label', undefined);
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent).toContain('Loading evidence');
  });

  it('reserves token geometry, wraps overflow and stays static even without reduced motion', () => {
    const css = styles();
    expect(css).toContain('min-block-size: calc(');
    expect(css).toContain('var(--eg-space-12)');
    expect(css).toContain('overflow-wrap: anywhere');
    expect(css).toContain('var(--eg-info-bg)');
    expect(css).not.toMatch(/(?:^|[;{])\s*(?:animation|transition)(?:-[\w-]+)?\s*:/);
    expect(root.querySelector('[style]')).toBeNull();
  });
});
