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
        expect(root.querySelector('[role="alert"] h2')?.textContent).toBe('Service unavailable');
        expect(root.querySelector('h2')?.hasAttribute('role')).toBe(false);
        expect(root.querySelector('.state-panel__description')?.textContent).toBe(failure.message);
        expect(root.querySelector('code')?.textContent).toBe(failure.code);
        expect(root.querySelector('.error-pattern__request-id')?.textContent).toContain(
          'request-001',
        );
        if (state === 'hover') {
          button.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
        } else if (state === 'focus-visible') {
          button.focus();
          expect(document.activeElement).toBe(button);
        } else if (state === 'active') {
          button.click();
          expect(events).toEqual(['retry']);
        } else if (state === 'disabled') {
          fixture.componentRef.setInput('retryDisabled', true);
          fixture.detectChanges();
          expect(button.disabled).toBe(true);
          button.click();
        }
        if (state !== 'active') expect(events).toEqual([]);
      });
    });
  }

  it('retains the announcement node on unrelated rendering and creates one for a new failure', () => {
    const alert = root.querySelector('[role="alert"]');
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    const heading = alert?.querySelector('h2');
    expect(heading?.hasAttribute('role')).toBe(false);
    fixture.detectChanges();
    fixture.componentRef.setInput('retryDisabled', true);
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')).toBe(alert);
    expect(root.querySelector('h2')).toBe(heading);
    fixture.componentRef.setInput('error', { ...failure, requestId: 'request-002' });
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')).not.toBe(alert);
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    expect(root.querySelector('[role="alert"] h2')).not.toBe(heading);
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

  it('keeps the default heading level at h2 and renders no other level', () => {
    expect(root.querySelector('.state-panel__title')?.tagName).toBe('H2');
    expect(root.querySelector('h3, h4')).toBeNull();
  });

  it('renders the requested heading level and drops the default h2', () => {
    fixture.componentRef.setInput('headingLevel', 3);
    fixture.detectChanges();
    expect(root.querySelector('h3.state-panel__title')?.textContent).toBe('Service unavailable');
    expect(root.querySelector('h2')).toBeNull();

    fixture.componentRef.setInput('headingLevel', 4);
    fixture.detectChanges();
    expect(root.querySelector('h4.state-panel__title')?.textContent).toBe('Service unavailable');
    expect(root.querySelector('h2, h3')).toBeNull();
  });

  it('applies the requested heading level to the empty variant without an announcement', () => {
    fixture.componentRef.setInput('error', null);
    fixture.componentRef.setInput('headingLevel', 3);
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.querySelector('h3.state-panel__title')?.textContent).toBe('Something went wrong');
    expect(root.querySelector('h2')).toBeNull();
  });

  it('exposes the request id through its test id on the existing metadata element', () => {
    const requestId = root.querySelector('[data-testid="error-request-id"]');
    expect(requestId?.textContent).toContain('request-001');
    expect(requestId?.classList.contains('error-pattern__request-id')).toBe(true);
    expect(requestId?.classList.contains('state-panel__metadata')).toBe(true);
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

  it('renders server message, machine code and request id as literal text', () => {
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
  });
});
