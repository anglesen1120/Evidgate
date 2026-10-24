// State sheet: docs/design/web/state-sheet.md#error-pattern
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { ApiError } from '../../../core/http/api-error';
import { RequestStateComponent } from './request-state.component';

function failure(overrides: Partial<ApiError> = {}): ApiError {
  return {
    kind: 'network',
    status: 0,
    code: 'network_error',
    message: 'Check your connection.',
    requestId: 'req-example',
    retryable: true,
    details: [],
    retryAfterSeconds: null,
    ...overrides,
  };
}

@Component({
  imports: [RequestStateComponent],
  template: `
    <app-request-state
      [status]="status()"
      [error]="error()"
      [empty]="empty()"
      [emptyTitle]="'No evidence yet'"
      [emptyDescription]="'Open the feature tree to inspect cases.'"
      [emptyActionLabel]="'Open feature tree'"
      [emptyActionHref]="href()"
      [retryEligible]="eligible()"
      [retryBusy]="busy()"
      (retry)="retries.push('retry')"
      (emptyAction)="actions.push('empty')"
    >
      <p data-testid="current-data">{{ projectedLabel }}</p>
    </app-request-state>
  `,
})
class Host {
  readonly projectedLabel = 'Current evidence';
  readonly status = signal<'loading' | 'ready' | 'error'>('ready');
  readonly error = signal<ApiError | null>(null);
  readonly empty = signal(false);
  readonly eligible = signal(false);
  readonly busy = signal(false);
  readonly href = signal<string | undefined>(undefined);
  readonly retries: string[] = [];
  readonly actions: string[] = [];
}

describe('RequestStateComponent route-level replacement', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('projects current content only when ready and nonempty, never while loading or failed', async () => {
    expect(root.querySelector('[data-testid="current-data"]')).not.toBeNull();
    host.status.set('loading');
    await fixture.whenStable();
    expect(root.querySelector('app-loading-pattern [aria-busy="true"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="current-data"]')).toBeNull();
    host.status.set('error');
    host.error.set(failure());
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.querySelector('[data-testid="current-data"]')).toBeNull();
    host.status.set('ready');
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="current-data"]')).not.toBeNull();
    expect(root.querySelector('app-error-pattern, app-loading-pattern')).toBeNull();
  });

  it('uses distinct non-enumerating 403 and 404 replacements without leaking envelope data', async () => {
    host.status.set('error');
    host.error.set(
      failure({
        kind: 'not_found',
        status: 404,
        message: 'Unknown id 123',
        code: 'unknown_123',
      }),
    );
    await fixture.whenStable();
    const unknownText = root.textContent;
    expect(root.querySelector('app-not-found-pattern')).not.toBeNull();
    host.error.set(
      failure({
        kind: 'not_found',
        status: 404,
        message: 'Foreign resource secret',
        code: 'tenant_secret',
        requestId: 'private-id',
      }),
    );
    await fixture.whenStable();
    expect(root.textContent).toBe(unknownText);
    host.error.set(
      failure({
        kind: 'forbidden',
        status: 403,
        message: 'Resource secret',
        code: 'private-code',
        requestId: 'private-id',
        details: [{ path: 'secret', reason: 'private-value' }],
      }),
    );
    await fixture.whenStable();
    expect(root.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(root.querySelector('app-not-found-pattern, app-error-pattern')).toBeNull();
    expect(root.textContent).not.toBe(unknownText);
    expect(root.textContent).not.toMatch(/secret|private|req-example/i);
    expect(root.querySelector('button')).toBeNull();
  });

  it('shows supplied Retry-After and waits for eligibility without emitting automatically', async () => {
    host.status.set('error');
    host.eligible.set(false);
    host.error.set(failure({ kind: 'rate_limited', status: 429, retryAfterSeconds: 30 }));
    await fixture.whenStable();
    expect(root.querySelector('app-retry-after')?.textContent).toContain('Retry after: 30 s');
    expect(root.querySelector('app-error-pattern')).toBeNull();
    expect(root.querySelector('button')?.disabled).toBe(true);
    root.querySelector('button')?.click();
    expect(host.retries).toEqual([]);
    host.eligible.set(true);
    await fixture.whenStable();
    expect(root.querySelector('button')?.disabled).toBe(false);
    expect(host.retries).toEqual([]);
    root.querySelector('button')?.click();
    expect(host.retries).toEqual(['retry']);
    host.busy.set(true);
    await fixture.whenStable();
    expect(root.querySelector('button')?.disabled).toBe(true);
  });

  it('disables a 429 retry with a supplied interval when retryEligible is not bound', async () => {
    const unbound = TestBed.createComponent(RequestStateComponent);
    unbound.componentRef.setInput('status', 'error');
    unbound.componentRef.setInput(
      'error',
      failure({ kind: 'rate_limited', status: 429, retryAfterSeconds: 30 }),
    );
    const retries: string[] = [];
    unbound.componentInstance.retry.subscribe(() => retries.push('retry'));
    await unbound.whenStable();
    const panel = unbound.nativeElement as HTMLElement;
    expect(panel.querySelector('app-retry-after')?.textContent).toContain('Retry after: 30 s');
    const button = panel.querySelector('button')!;
    expect(button.disabled).toBe(true);
    button.click();
    expect(retries).toEqual([]);
  });

  it('uses rate-limit fallback without guessing a missing interval and distinguishes 503', async () => {
    host.status.set('error');
    host.eligible.set(true);
    host.error.set(failure({ kind: 'rate_limited', status: 429 }));
    await fixture.whenStable();
    expect(root.textContent).toContain('The server did not say how long to wait');
    expect(root.querySelector('button')?.disabled).toBe(false);
    host.error.set(failure({ kind: 'unavailable', status: 503, retryAfterSeconds: 15 }));
    await fixture.whenStable();
    expect(root.querySelector('app-retry-after h2')?.textContent).toContain('503');
    host.error.set(failure({ kind: 'unavailable', status: 503 }));
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.querySelector('app-retry-after')).toBeNull();
    expect(host.retries).toEqual([]);
  });

  it.each([
    { eligible: false, busy: false, disabled: true },
    { eligible: true, busy: true, disabled: true },
    { eligible: true, busy: false, disabled: false },
  ])(
    'forwards generic retry only on native activation: $eligible / $busy',
    async ({ eligible, busy, disabled }) => {
      host.status.set('error');
      host.error.set(failure());
      host.eligible.set(eligible);
      host.busy.set(busy);
      await fixture.whenStable();
      expect(root.querySelector('button')?.disabled).toBe(disabled);
      expect(host.retries).toEqual([]);
      root.querySelector('button')?.click();
      expect(host.retries).toEqual(disabled ? [] : ['retry']);
    },
  );

  it('keeps empty neutral, forwards its explicit command and preserves real navigation', async () => {
    host.empty.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="current-data"]')).toBeNull();
    expect(root.querySelector('app-empty-pattern h2')?.textContent).toBe('No evidence yet');
    expect(root.textContent).not.toMatch(/\bPASS\b/);
    expect(root.querySelector('[data-tone="success"], [class*="success"]')).toBeNull();
    expect(host.actions).toEqual([]);
    root.querySelector('button')?.click();
    expect(host.actions).toEqual(['empty']);
    host.href.set('/features');
    await fixture.whenStable();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/features');
    expect(root.querySelector('button')).toBeNull();
    expect(host.retries).toEqual([]);
  });

  it('keeps a null error in the generic error branch without projecting stale content', async () => {
    host.status.set('error');
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.querySelector('[data-testid="current-data"], button')).toBeNull();
  });
});
