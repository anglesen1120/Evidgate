// State sheet: docs/design/web/state-sheet.md#403-pattern
import { provideZonelessChangeDetection, reflectComponentType } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ForbiddenPatternComponent } from './forbidden-pattern.component';

const rows = [
  ['default', 'implemented — fixed authorized-scope instruction'],
  ['hover', 'n/a — render-only panel'],
  ['focus-visible', 'n/a — render-only panel'],
  ['active', 'n/a — render-only panel'],
  ['disabled', 'n/a — render-only panel'],
  ['loading', 'n/a — route-level replacement'],
  ['empty', 'n/a — route-level replacement'],
  ['error', 'implemented — fixed authorized-scope instruction'],
  ['success', 'n/a — route-level replacement'],
] as const;

describe('ForbiddenPatternComponent', () => {
  let fixture: ComponentFixture<ForbiddenPatternComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ForbiddenPatternComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ForbiddenPatternComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  for (const [state, contract] of rows) {
    describe(`${state}: ${contract}`, () => {
      it('renders only fixed scope-safe text without controls, hints or resource inputs', () => {
        expect(root.querySelector('h2')?.textContent).toBe('Not authorized');
        expect(root.textContent).toContain(
          'Ask a project administrator for access to this workspace.',
        );
        expect(root.textContent).not.toMatch(/tenant|organization|exists/i);
        expect(
          root.querySelector('input, button, a, [tabindex], [role="button"], [aria-disabled]'),
        ).toBeNull();
        expect(root.querySelector('[aria-live], [role="alert"], [aria-busy]')).toBeNull();
        expect(reflectComponentType(ForbiddenPatternComponent)?.inputs).toEqual([]);
        expect(root.querySelector('[class*="success"]')).toBeNull();
        expect(root.textContent).not.toMatch(/\b(PASS|FAIL)\b/);
      });
    });
  }

  it('cannot render a resource id, name, server message or code supplied as inert host attributes', () => {
    const before = root.textContent;
    for (const attribute of ['id', 'name', 'message', 'code']) {
      root.setAttribute(attribute, 'protected-record-<script>private</script>');
    }
    fixture.detectChanges();
    expect(root.textContent).toBe(before);
    expect(root.textContent).not.toContain('protected-record');
    expect(root.querySelector('script')).toBeNull();
  });

  it('uses the theme-aware panel recipe without clipping or inline styles', () => {
    const css = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .filter((text) => text.includes('.state-panel'))
      .join('\n');
    expect(css).toContain('var(--eg-surface)');
    expect(css).toContain('var(--eg-text-muted)');
    expect(css).toContain('overflow-wrap: anywhere');
    expect(root.querySelector('[style], svg:not([aria-hidden="true"])')).toBeNull();
  });
});
