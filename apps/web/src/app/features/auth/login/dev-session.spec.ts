import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';

import { SessionStore } from '../../../core/session/session.store';
import { AuthFlow } from '../auth-flow';
import { DevSession } from './dev-session';

describe('DevSession', () => {
  let http: HttpTestingController;
  let subject: DevSession;
  const state = signal('anonymous');
  const session = {
    reauth: vi.fn(),
    reload: vi.fn<() => Promise<void>>(),
    sessionState: state,
  };
  const router = { navigateByUrl: vi.fn(async () => true) };

  beforeEach(() => {
    vi.clearAllMocks();
    state.set('anonymous');
    session.reload.mockImplementation(async () => {
      state.set('authenticated');
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        DevSession,
        { provide: SessionStore, useValue: session },
        { provide: AuthFlow, useValue: { returnUrl: signal('/orgs/o/projects/p/overview?tab=runs') } },
        { provide: Router, useValue: router },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    subject = TestBed.inject(DevSession);
  });

  afterEach(() => http.verify());

  it('success: clears the signed-out latch and awaits a fresh session probe before navigation', async () => {
    let finishProbe: () => void = () => undefined;
    session.reload.mockImplementation(
      () => new Promise<void>((resolve) => {
        finishProbe = () => {
          state.set('authenticated');
          resolve();
        };
      }),
    );
    const attempt = subject.signIn(crypto.randomUUID());
    expect(session.reauth).not.toHaveBeenCalled();
    http.expectOne('/v1/dev/session').flush(null, { status: 204, statusText: 'No Content' });
    await Promise.resolve();
    expect(session.reauth).toHaveBeenCalledOnce();
    expect(session.reload).toHaveBeenCalledOnce();
    expect(session.reauth.mock.invocationCallOrder[0]).toBeLessThan(
      session.reload.mock.invocationCallOrder[0] ?? 0,
    );
    expect(router.navigateByUrl).not.toHaveBeenCalled();
    finishProbe();
    await attempt;
    expect(router.navigateByUrl).toHaveBeenCalledWith('/orgs/o/projects/p/overview?tab=runs', {
      replaceUrl: true,
    });
  });

  it('does not navigate when the accepted cookie still probes as anonymous', async () => {
    session.reload.mockImplementation(async () => undefined);
    const attempt = subject.signIn(crypto.randomUUID());
    const outcome = expect(attempt).rejects.toThrow('Session verification did not complete.');
    http.expectOne('/v1/dev/session').flush(null, { status: 204, statusText: 'No Content' });
    await outcome;
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does not navigate when the session probe is unavailable', async () => {
    session.reload.mockRejectedValue(new Error('Unavailable'));
    const attempt = subject.signIn(crypto.randomUUID());
    const outcome = expect(attempt).rejects.toThrow('Unavailable');
    http.expectOne('/v1/dev/session').flush(null, { status: 204, statusText: 'No Content' });
    await outcome;
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does not re-open or probe a session when the secret is refused', async () => {
    const attempt = subject.signIn(crypto.randomUUID());
    const outcome = expect(attempt).rejects.toMatchObject({ status: 401 });
    http.expectOne('/v1/dev/session').flush({}, { status: 401, statusText: 'Unauthorized' });
    await outcome;
    expect(session.reauth).not.toHaveBeenCalled();
    expect(session.reload).not.toHaveBeenCalled();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });
});
