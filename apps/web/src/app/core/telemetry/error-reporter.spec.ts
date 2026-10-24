// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: client error reporting with
// PII/secret scrubbing, and telemetry loss must never break the app): the two error sources — the
// replacing `ErrorHandler` and the window's `error`/`unhandledrejection` — and the shared budget
// that keeps an error loop from becoming the outage.
//
// The service is the real `TelemetryService` with `reportError` spied on, not a hand-written
// double: the reporter and the facade agree on one interface, and the rate-limit budget is keyed
// by the service instance, so a fresh TestBed is a fresh budget without a test-only reset.
//
// Reports are asserted as whole objects where possible: an `ApiRequestError` must contribute
// identifiers only, and comparing the object catches a field added later that would carry server
// text.
import { ErrorHandler } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MockInstance } from 'vitest';

import { ApiRequestError, type ApiError } from '../http/api-error';
import { registerGlobalErrorListeners, TelemetryErrorHandler } from './error-reporter';
import { TelemetryService } from './telemetry.service';
import type { ErrorReport } from './telemetry-sdk';

/** A request id the server could have echoed. */
const REQUEST_ID = '3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b';

/** Server text that must never reach a report. */
const SERVER_TEXT = 'the run 9a1b belongs to another project';

interface Harness {
  readonly service: TelemetryService;
  readonly handler: ErrorHandler;
  readonly reports: ErrorReport[];
}

/** A TestBed whose `ErrorHandler` is the one `provideTelemetry()` registers. */
function createHarness(): Harness {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [{ provide: ErrorHandler, useClass: TelemetryErrorHandler }],
  });
  const service = TestBed.inject(TelemetryService);
  const reports: ErrorReport[] = [];
  vi.spyOn(service, 'reportError').mockImplementation((report: ErrorReport) => {
    reports.push(report);
  });
  return { service, handler: TestBed.inject(ErrorHandler), reports };
}

/** An `ApiError` of the given shape, with a message that must not be exported. */
function apiError(overrides: Partial<ApiError> = {}): ApiError {
  return {
    kind: 'not_found',
    status: 404,
    code: 'run_not_found',
    message: SERVER_TEXT,
    requestId: REQUEST_ID,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
    ...overrides,
  };
}

describe('TelemetryErrorHandler', () => {
  let consoleError: MockInstance;

  beforeEach(() => {
    consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  afterEach(() => {
    consoleError.mockRestore();
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('keeps Angular console output and records the error type and message', () => {
    const harness = createHarness();
    const error = new TypeError('x is not a function');

    harness.handler.handleError(error);

    expect(consoleError).toHaveBeenCalledWith('ERROR', error);
    expect(harness.reports).toEqual([
      { source: 'angular', type: 'TypeError', message: 'x is not a function' },
    ]);
  });

  it('reports a thrown string as an Error-typed report with the string as its message', () => {
    const harness = createHarness();

    harness.handler.handleError('something broke');

    expect(harness.reports).toEqual([
      { source: 'angular', type: 'Error', message: 'something broke' },
    ]);
  });

  it('reports an ApiRequestError by its identifiers only', () => {
    const harness = createHarness();

    harness.handler.handleError(new ApiRequestError(apiError()));

    expect(harness.reports).toEqual([
      {
        source: 'api',
        type: 'ApiRequestError',
        status: 404,
        errorKind: 'not_found',
        errorCode: 'run_not_found',
        retryable: false,
        requestId: REQUEST_ID,
      },
    ]);
    expect(JSON.stringify(harness.reports)).not.toContain(SERVER_TEXT);
  });

  it('omits the code and the request id an ApiError does not have', () => {
    const harness = createHarness();
    const network = apiError({ kind: 'network', status: 0, code: '', requestId: null });

    harness.handler.handleError(new ApiRequestError(network));

    expect(harness.reports).toEqual([
      { source: 'api', type: 'ApiRequestError', status: 0, errorKind: 'network', retryable: false },
    ]);
  });

  it('drops reports beyond ten in a rolling minute, then allows them again', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-24T10:00:00Z'));
    const harness = createHarness();

    for (let index = 0; index < 12; index += 1) {
      harness.handler.handleError(new Error(`failure ${index}`));
    }

    expect(harness.reports).toHaveLength(10);
    // Console output is never rate limited: a developer still sees every error.
    expect(consoleError).toHaveBeenCalledTimes(12);

    vi.advanceTimersByTime(61_000);
    harness.handler.handleError(new Error('after the window'));

    expect(harness.reports).toHaveLength(11);
    expect(harness.reports[10]?.message).toBe('after the window');
  });
});

describe('registerGlobalErrorListeners', () => {
  let consoleError: MockInstance;
  let unregister: (() => void) | null = null;

  beforeEach(() => {
    consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  afterEach(() => {
    unregister?.();
    unregister = null;
    consoleError.mockRestore();
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('reports a window error with its type and message', () => {
    const harness = createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    window.dispatchEvent(
      new ErrorEvent('error', {
        error: new RangeError('offset is out of range'),
        message: 'ignored',
      }),
    );

    expect(harness.reports).toEqual([
      { source: 'window.error', type: 'RangeError', message: 'offset is out of range' },
    ]);
  });

  it('falls back to the event message when the thrown value is not an Error', () => {
    const harness = createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    window.dispatchEvent(new ErrorEvent('error', { error: null, message: 'Script error.' }));

    expect(harness.reports).toEqual([
      { source: 'window.error', type: 'Error', message: 'Script error.' },
    ]);
  });

  it('reports an unhandled rejection', () => {
    const harness = createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    window.dispatchEvent(
      new PromiseRejectionEvent('unhandledrejection', {
        promise: Promise.resolve(),
        reason: new Error('the query failed'),
      }),
    );

    expect(harness.reports).toEqual([
      { source: 'unhandledrejection', type: 'Error', message: 'the query failed' },
    ]);
  });

  it('stops reporting once unregistered, and removes both listeners', () => {
    const harness = createHarness();
    const removed = vi.spyOn(window, 'removeEventListener');
    const stop = registerGlobalErrorListeners(window, harness.service);

    stop();
    // A rejection, not an `error` event: a dispatched error that nothing cancels is reported by
    // jsdom as an uncaught exception, which would fail the run instead of this assertion.
    window.dispatchEvent(
      new PromiseRejectionEvent('unhandledrejection', {
        promise: Promise.resolve(),
        reason: new Error('after unregister'),
      }),
    );

    expect(harness.reports).toHaveLength(0);
    expect(removed).toHaveBeenCalledWith('error', expect.any(Function));
    expect(removed).toHaveBeenCalledWith('unhandledrejection', expect.any(Function));
  });

  it('shares one budget with the Angular handler', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-24T10:00:00Z'));
    const harness = createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    for (let index = 0; index < 10; index += 1) {
      harness.handler.handleError(new Error(`angular ${index}`));
    }
    window.dispatchEvent(new ErrorEvent('error', { error: new Error('window one') }));

    expect(harness.reports).toHaveLength(10);
    expect(harness.reports.map((report) => report.source)).not.toContain('window.error');
  });
});
