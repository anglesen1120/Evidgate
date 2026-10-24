// State sheet: docs/design/web/state-sheet.md#error-pattern
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { ApiError } from '../../../core/http/api-error';
import { ErrorPatternComponent } from './error-pattern.component';

const rows = [
  ['default', 'implemented — typed failed read'],
  ['hover', 'implemented — secondary retry only'],
  ['focus-visible', 'implemented — native retry only'],
  ['active', 'implemented — explicit retry output'],
  ['disabled', 'implemented — native retryDisabled guard'],
  ['loading', 'n/a — route-level replacement'],
  ['empty', 'n/a — route-level replacement'],
  ['error', 'implemented — typed failed read'],
  ['success', 'n/a — route-level replacement'],
] as const;

const failure: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'dependency_busy',
  message: 'The dependency is busy.',
  requestId: 'request-001',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};

function styles(): string {
  return Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .filter((css) => css.includes('.state-panel') || css.includes('.error-pattern__request-id'))
    .join('\n');
}

describe('ErrorPatternComponent', () => {
  let fixture: ComponentFixture<ErrorPatternComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ErrorPatternComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ErrorPatternComponent);
    fixture.componentRef.setInput('error', failure);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  for (const [state, contract] of rows) {
    describe(`${state}: ${contract}`, () => {
      it('keeps failure context visible and never retries on focus, hover or render', () => {
        const events: string[] = [];
        fixture.componentInstance.retry.subscribe(() => events.push('retry'));
        const button = root.querySelector('button')!;
        expect(button.type).toBe('button');
        expect(button.classList.contains('state-panel__action')).toBe(true);
        expect(root.querySelector('section')?.hasAttribute('tabindex')).toBe(false);
        expect(root.querySelector('[aria-disabled], [class*="success"], [aria-busy]')).toBeNull();
        expect(root.querySelector('[role="alert"]')?.textContent).toBe('Service unavailable');
        expect(root.querySelector('.state-panel__description')?.textContent).toBe(failure.message);
        expect(root.querySelector('code')?.textContent).toBe(failure.code);
        expect(root.querySelector('.error-pattern__request-id')?.textContent).toContain(
          'request-001',
        );
        if (state === 'hover') {
          button.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
          expect(styles()).toContain(':hover');
          expect(styles()).toContain('var(--eg-surface-raised)');
        } else if (state === 'focus-visible') {
          button.focus();
          expect(document.activeElement).toBe(button);
          expect(styles()).toContain('outline: var(--eg-focus-width) solid var(--eg-focus)');
          expect(styles()).toContain('outline-offset: var(--eg-focus-offset)');
        } else if (state === 'active') {
          button.click();
          expect(events).toEqual(['retry']);
        } else if (state === 'disabled') {
          fixture.componentRef.setInput('retryDisabled', true);
          fixture.detectChanges();
          expect(button.disabled).toBe(true);
          button.click();
          expect(styles()).toContain('var(--eg-neutral-bg)');
        }
        if (state !== 'active') expect(events).toEqual([]);
      });
    });
  }

  it('retains the announcement node on unrelated rendering and creates one for a new failure', () => {
    const alert = root.querySelector('[role="alert"]');
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    fixture.detectChanges();
    fixture.componentRef.setInput('retryDisabled', true);
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')).toBe(alert);
    fixture.componentRef.setInput('error', { ...failure, requestId: 'request-002' });
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')).not.toBe(alert);
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    expect(root.querySelector('.error-pattern__request-id')?.textContent).toContain('request-002');
  });

  it('renders a generic null panel without retry, diagnostic content or a failure announcement', () => {
    fixture.componentRef.setInput('error', null);
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent).toBe('Something went wrong');
    expect(
      root.querySelector('button, code, .error-pattern__request-id, [role="alert"]'),
    ).toBeNull();
  });

  it('does not offer retry for a non-retryable failure, even when retryDisabled is false', () => {
    fixture.componentRef.setInput('error', { ...failure, kind: 'validation', retryable: false });
    fixture.detectChanges();
    expect(root.querySelector('button')).toBeNull();
    expect(root.querySelector('h2')?.textContent).toBe('Something went wrong');
  });

  it.each([
    ['network', 'Network unavailable'],
    ['timeout', 'Request timed out'],
    ['unavailable', 'Service unavailable'],
    ['unexpected', 'Something went wrong'],
  ] as const)('uses only typed kind %s for the heading', (kind, heading) => {
    fixture.componentRef.setInput('error', { ...failure, kind, status: 404, code: 'forbidden' });
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent).toBe(heading);
  });

  it('omits a missing request id and exposes no request headers, bodies or details', () => {
    fixture.componentRef.setInput('error', {
      ...failure,
      requestId: null,
      details: [{ field: 'private-body', reason: 'private-header' }],
    });
    fixture.detectChanges();
    expect(root.querySelector('.error-pattern__request-id')).toBeNull();
    expect(root.textContent).not.toMatch(/private-body|private-header/);
  });

  it('renders server message, machine code and request id as selectable literal text', () => {
    const untrusted = 'Lỗi yêu cầu '.repeat(40) + '<script>window.injected=true</script>';
    fixture.componentRef.setInput('error', {
      ...failure,
      message: untrusted,
      code: untrusted,
      requestId: untrusted,
    });
    fixture.detectChanges();
    expect(root.querySelector('.state-panel__description')?.textContent).toBe(untrusted);
    expect(root.querySelector('code')?.textContent).toBe(untrusted);
    expect(root.querySelector('.error-pattern__request-id')?.textContent).toContain(untrusted);
    expect(root.querySelector('script, [style]')).toBeNull();
    expect(styles()).toContain('font-family: var(--eg-font-mono)');
    expect(styles()).toContain('user-select: text');
    expect(styles()).toContain('overflow-wrap: anywhere');
  });
});
