import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';

import { provideApi } from '../../../core/http/api-client';
import { SessionStore } from '../../../core/session/session.store';
import { AuthFlow } from '../auth-flow';
import { DevSignInComponent } from './dev-sign-in.component';

describe('DevSignInComponent', () => {
  let fixture: ComponentFixture<DevSignInComponent>;
  let root: HTMLElement;
  let http: HttpTestingController;
  const session = {
    reauth: vi.fn(),
    reload: vi.fn(async () => undefined),
    sessionState: signal('authenticated'),
  };
  const router = { navigateByUrl: vi.fn(async () => true) };

  function element<T extends Element>(selector: string): T {
    const found = root.querySelector<T>(selector);
    if (!found) {
      throw new Error(`Missing ${selector}`);
    }
    return found;
  }

  function open(): void {
    const details = element<HTMLDetailsElement>('details');
    details.open = true;
    details.dispatchEvent(new Event('toggle'));
    fixture.detectChanges();
  }

  function submit(value = crypto.randomUUID()): string {
    element<HTMLInputElement>('input').value = value;
    element<HTMLFormElement>('form').dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    return value;
  }

  beforeEach(async () => {
    vi.clearAllMocks();
    session.sessionState.set('authenticated');
    await TestBed.configureTestingModule({
      imports: [DevSignInComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideApi(),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
        { provide: SessionStore, useValue: session },
        { provide: AuthFlow, useValue: { returnUrl: signal('/workspace') } },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DevSignInComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => {
    http.verify();
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
    vi.restoreAllMocks();
  });

  it('idle: reveals a named password form only when the native disclosure is opened', () => {
    expect(root.querySelector('input')).toBeNull();
    expect(element('summary').textContent).toContain(fixture.componentInstance.heading);
    open();
    const input = element<HTMLInputElement>('input');
    expect(input.type).toBe('password');
    expect(input.autocomplete).toBe('off');
    expect(input.required).toBe(true);
    expect(element('label').getAttribute('for')).toBe(input.id);
    expect(element('form').getAttribute('aria-labelledby')).toBe('dev-sign-in-heading');
    expect(element<HTMLButtonElement>('button').disabled).toBe(false);
    expect(element('[role="alert"]').textContent?.trim()).toBe('');
  });

  it('refuses an empty secret without making a request', () => {
    open();
    submit('');
    http.expectNone('/v1/dev/session');
    expect(session.reauth).not.toHaveBeenCalled();
  });

  it('submitting: sends credentials and XSRF once, clears the input, and announces busy state', async () => {
    document.cookie = 'XSRF-TOKEN=development-xsrf; path=/';
    open();
    const input = element<HTMLInputElement>('input');
    input.focus();
    const value = submit();
    expect(input.value).toBe('');
    expect(input.readOnly).toBe(true);
    expect(document.activeElement).toBe(input);
    expect(element<HTMLButtonElement>('button').disabled).toBe(true);
    expect(element('[role="status"]').textContent).toContain(fixture.componentInstance.submitting);
    element<HTMLFormElement>('form').dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );
    const request = http.expectOne('/v1/dev/session');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ secret: value });
    expect(request.request.withCredentials).toBe(true);
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('development-xsrf');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/workspace', { replaceUrl: true });
  });

  it('wrong secret: announces 401 inline and accepts a new attempt', async () => {
    open();
    submit();
    http.expectOne('/v1/dev/session').flush({}, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element('[role="alert"]').textContent).toContain(fixture.componentInstance.wrongSecret);
    expect(element('input').getAttribute('aria-invalid')).toBe('true');
    expect(element('input').getAttribute('aria-describedby')).toContain('dev-sign-in-error');
    expect(element<HTMLButtonElement>('button').disabled).toBe(false);
    expect(session.reload).not.toHaveBeenCalled();
    submit();
    http.expectOne('/v1/dev/session').flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();
    expect(session.reload).toHaveBeenCalledOnce();
  });

  it.each([404, 500, 503, 0])('unavailable: maps status %s to the disabled-server message', async (status) => {
    open();
    submit();
    const request = http.expectOne('/v1/dev/session');
    if (status === 0) {
      request.error(new ProgressEvent('error'));
    } else {
      request.flush({}, { status, statusText: 'Unavailable' });
    }
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element('[role="alert"]').textContent).toContain(fixture.componentInstance.unavailable);
    expect(element('[role="status"]').textContent?.trim()).toBe('');
    expect(element<HTMLButtonElement>('button').disabled).toBe(false);
    expect(session.reload).not.toHaveBeenCalled();
  });

  it('does not persist, log, render, or place the submitted secret in navigation', async () => {
    open();
    const store = vi.spyOn(Storage.prototype, 'setItem');
    const logs = (['log', 'info', 'warn', 'error', 'debug'] as const).map((method) =>
      vi.spyOn(console, method).mockImplementation(() => undefined),
    );
    const value = submit();
    http.expectOne('/v1/dev/session').flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();
    expect(store).not.toHaveBeenCalled();
    for (const log of logs) {
      expect(log).not.toHaveBeenCalled();
    }
    expect(element<HTMLInputElement>('input').value).toBe('');
    expect(root.innerHTML).not.toContain(value);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/workspace', { replaceUrl: true });
  });
});
