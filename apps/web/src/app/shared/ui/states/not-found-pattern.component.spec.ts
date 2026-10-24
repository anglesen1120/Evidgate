// State sheet: docs/design/web/state-sheet.md#404-pattern
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  reflectComponentType,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { NotFoundPatternComponent } from './not-found-pattern.component';

const rows = [
  ['default', 'implemented — non-enumerating fixed explanation'],
  ['hover', 'n/a — render-only panel'],
  ['focus-visible', 'n/a — render-only panel'],
  ['active', 'n/a — render-only panel'],
  ['disabled', 'n/a — render-only panel'],
  ['loading', 'n/a — route-level replacement'],
  ['empty', 'n/a — route-level replacement'],
  ['error', 'implemented — non-enumerating fixed explanation'],
  ['success', 'n/a — route-level replacement'],
] as const;

@Component({
  selector: 'app-not-found-test-host',
  standalone: true,
  imports: [NotFoundPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-not-found-pattern [attr.data-resource-id]="resourceId">{{
      serverMessage
    }}</app-not-found-pattern>
  `,
})
class NotFoundTestHost {
  resourceId = 'unknown-id';
  readonly serverMessage = 'protected resource exists elsewhere';
}

describe('NotFoundPatternComponent', () => {
  let fixture: ComponentFixture<NotFoundPatternComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NotFoundPatternComponent, NotFoundTestHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(NotFoundPatternComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  for (const [state, contract] of rows) {
    describe(`${state}: ${contract}`, () => {
      it('renders the same fixed explanation without resource inputs or interactive modes', () => {
        expect(root.querySelector('h2')?.textContent).toBe('Not found');
        expect(root.textContent).toContain(
          'This page does not exist or you do not have access to it.',
        );
        expect(reflectComponentType(NotFoundPatternComponent)?.inputs).toEqual([]);
        expect(
          root.querySelector('input, button, a, [tabindex], [role="button"], [aria-disabled]'),
        ).toBeNull();
        expect(root.querySelector('[aria-live], [role="alert"], [aria-busy]')).toBeNull();
        expect(root.querySelector('[class*="success"]')).toBeNull();
        expect(root.textContent).not.toMatch(/\b(PASS|FAIL)\b|protected resource|elsewhere/);
      });
    });
  }

  it('is byte-identical for direct creation, unknown ids, foreign ids and separate instances', () => {
    const direct = root.textContent;
    const host = TestBed.createComponent(NotFoundTestHost);
    host.detectChanges();
    const nested = (host.nativeElement as HTMLElement).querySelector('app-not-found-pattern')!;
    expect(nested.textContent).toBe(direct);
    host.componentInstance.resourceId = 'foreign-project-secret';
    host.changeDetectorRef.markForCheck();
    host.detectChanges();
    expect(nested.textContent).toBe(direct);
    expect(nested.textContent).not.toContain('foreign-project-secret');
    expect(nested.textContent).not.toContain(host.componentInstance.serverMessage);
    const second = TestBed.createComponent(NotFoundPatternComponent);
    second.detectChanges();
    expect((second.nativeElement as HTMLElement).textContent).toBe(direct);
  });

  it('ignores untrusted host attributes instead of rendering resource payloads', () => {
    const before = root.textContent;
    for (const attribute of ['id', 'name', 'message', 'code']) {
      root.setAttribute(attribute, 'foreign-record-<script>private</script>');
    }
    fixture.detectChanges();
    expect(root.textContent).toBe(before);
    expect(root.querySelector('script')).toBeNull();
  });

  it('uses theme tokens and wrapping, with no inline styling or unhidden decorative icon', () => {
    const css = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .filter((text) => text.includes('.state-panel'))
      .join('\n');
    expect(css).toContain('var(--eg-surface)');
    expect(css).toContain('overflow-wrap: anywhere');
    expect(root.querySelector('[style], svg:not([aria-hidden="true"])')).toBeNull();
  });
});
