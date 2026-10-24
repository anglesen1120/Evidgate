// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: client error reporting with
// PII/secret scrubbing, and telemetry loss must never break the app): the two error sources — the
// replacing `ErrorHandler` and the window's `error`/`unhandledrejection` — and the shared budget
// that keeps an error loop from becoming the outage.
//
// The service is the real `TelemetryService` with `reportError` spied on, not a hand-written
// double: the reporter and the facade agree on one interface, the rate-limit budget is keyed by
// the service instance, so a fresh TestBed is a fresh budget without a test-only reset, and the
// runtime the budget gate reads is the facade's own. The chunk arrives through a loader whose
// promise this spec holds open, which is the only way to observe "the chunk has not loaded yet"
// and then "the runtime is ready" without a second injection.
//
// Reports are asserted as whole objects where possible: an `ApiRequestError` must contribute
// identifiers only, and comparing the object catches a field added later that would carry server
// text.
import { ErrorHandler } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InMemorySpanExporter } from '@opentelemetry/sdk-trace';
import type { MockInstance } from 'vitest';

import { ApiRequestError, type ApiError } from '../http/api-error';
import { registerGlobalErrorListeners, TelemetryErrorHandler } from './error-reporter';
import { startTelemetrySdk } from './telemetry-sdk';
import type { ErrorReport, TelemetrySdkOptions } from './telemetry-sdk';
import type { TelemetryConfig } from './telemetry-config';
import { TELEMETRY_SDK_LOADER, TelemetryService } from './telemetry.service';
import type { TelemetrySdkLoader } from './telemetry.service';

/** A request id the server could have echoed. */
const REQUEST_ID = '3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b';

/** Server text that must never reach a report. */
const SERVER_TEXT = 'the run 9a1b belongs to another project';

interface Harness {
  readonly service: TelemetryService;
  readonly handler: ErrorHandler;
  readonly reports: ErrorReport[];
  /**
   * Settles the loader's promise: the chunk arrives, the facade's runtime becomes ready and the
   * budget starts counting. A harness created with `start: false` holds it open.
   */
  arrive(): Promise<void>;
}

/** Lets the loader's promise and the facade's continuation run. */
async function settle(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

/** The chunk's entry point, started against an in-memory exporter instead of the collector. */
function sdkModule(exporter: InMemorySpanExporter): {
  startTelemetrySdk: typeof startTelemetrySdk;
} {
  return {
    startTelemetrySdk: (config: TelemetryConfig, sdk?: TelemetrySdkOptions) =>
      startTelemetrySdk(config, { ...sdk, exporter }),
  };
}

/**
 * A TestBed whose `ErrorHandler` is the one `provideTelemetry()` registers, and a facade whose
 * telemetry chunk arrives from a loader this spec decides about. `start: false` leaves the
 * loader's promise pending — the chunk is still in flight — until `arrive()` is awaited.
 */
async function createHarness(options: { readonly start?: boolean } = {}): Promise<Harness> {
  const exporter = new InMemorySpanExporter();
  let openGate: (() => void) | null = null;
  // The deferred resolver by hand: `Promise.withResolvers` is ES2024 and this project compiles
  // with `lib: ES2022`.
  const gate = new Promise<void>((resolve) => {
    openGate = resolve;
  });
  const loader: TelemetrySdkLoader = async () => {
    await gate;
    return sdkModule(exporter);
  };
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: ErrorHandler, useClass: TelemetryErrorHandler },
      { provide: TELEMETRY_SDK_LOADER, useValue: loader },
    ],
  });
  const service = TestBed.inject(TelemetryService);
  const reports: ErrorReport[] = [];
  vi.spyOn(service, 'reportError').mockImplementation((report: ErrorReport) => {
    reports.push(report);
  });
  const handler = TestBed.inject(ErrorHandler);
  const arrive = async (): Promise<void> => {
    openGate?.();
    openGate = null;
    await settle();
  };
  service.start();
  if (options.start !== false) {
    await arrive();
  }
  return { service, handler, reports, arrive };
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

  it('keeps Angular console output and records the error type, never its message', async () => {
    const harness = await createHarness();
    const error = new TypeError('x is not a function');

    harness.handler.handleError(error);

    expect(consoleError).toHaveBeenCalledWith('ERROR', error);
    expect(harness.reports).toEqual([{ source: 'angular', type: 'TypeError' }]);
    // D-S69-Q2: no message text at all, so not even the thrown text reaches the report.
    expect(JSON.stringify(harness.reports)).not.toContain('x is not a function');
  });

  it('reports a thrown string as an Error-typed report without the string', async () => {
    const harness = await createHarness();

    harness.handler.handleError('something broke');

    expect(harness.reports).toEqual([{ source: 'angular', type: 'Error' }]);
    expect(JSON.stringify(harness.reports)).not.toContain('something broke');
  });

  it('reports an ApiRequestError by its identifiers only', async () => {
    const harness = await createHarness();

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

  it('omits the code and the request id an ApiError does not have', async () => {
    const harness = await createHarness();
    const network = apiError({ kind: 'network', status: 0, code: '', requestId: null });

    harness.handler.handleError(new ApiRequestError(network));

    expect(harness.reports).toEqual([
      { source: 'api', type: 'ApiRequestError', status: 0, errorKind: 'network', retryable: false },
    ]);
  });

  it('drops reports beyond ten in a rolling minute, then allows them again', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-24T10:00:00Z'));
    const harness = await createHarness();

    for (let index = 0; index < 12; index += 1) {
      harness.handler.handleError(new RangeError(`failure ${index}`));
    }

    expect(harness.reports).toHaveLength(10);
    // Console output is never rate limited: a developer still sees every error.
    expect(consoleError).toHaveBeenCalledTimes(12);

    vi.advanceTimersByTime(61_000);
    harness.handler.handleError(new SyntaxError('after the window'));

    expect(harness.reports).toHaveLength(11);
    // A report has no text (D-S69-Q2), so the class name is what says which one arrived last.
    expect(harness.reports[10]?.type).toBe('SyntaxError');
    expect(harness.reports.slice(0, 10).every((report) => report.type === 'RangeError')).toBe(true);
  });

  it('spends no budget while the chunk has not loaded, and ten reports pass once it has', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-24T10:00:00Z'));
    // The loader's promise stays pending: the chunk is still in flight, so the facade has no
    // runtime to carry a report.
    const harness = await createHarness({ start: false });

    for (let index = 0; index < 20; index += 1) {
      harness.handler.handleError(new Error(`before the chunk ${index}`));
    }

    expect(harness.service.runtime).toBeNull();
    expect(harness.reports).toHaveLength(0);
    // The console still shows every error: reporting is what waits for telemetry, not the app.
    expect(consoleError).toHaveBeenCalledTimes(20);

    await harness.arrive();

    for (let index = 0; index < 10; index += 1) {
      harness.handler.handleError(new RangeError(`after the chunk ${index}`));
    }
    harness.handler.handleError(new SyntaxError('after the chunk 10'));

    // The twenty reports that could not be exported spent nothing: the ten exportable ones all
    // pass, and only the eleventh — the one whose type differs, since a report carries no text —
    // is dropped.
    expect(harness.service.runtime).not.toBeNull();
    expect(harness.reports).toHaveLength(10);
    expect(harness.reports.every((report) => report.type === 'RangeError')).toBe(true);
    expect(consoleError).toHaveBeenCalledTimes(31);
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

  it('reports a window error with its type, and reads no message from the event', async () => {
    const harness = await createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    window.dispatchEvent(
      new ErrorEvent('error', {
        error: new RangeError('offset is out of range'),
        message: 'ignored',
      }),
    );

    expect(harness.reports).toEqual([{ source: 'window.error', type: 'RangeError' }]);
    expect(JSON.stringify(harness.reports)).not.toContain('offset is out of range');
    expect(JSON.stringify(harness.reports)).not.toContain('ignored');
  });

  it('reports an Error type when the event threw no Error, still without text', async () => {
    const harness = await createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    window.dispatchEvent(new ErrorEvent('error', { error: null, message: 'Script error.' }));

    expect(harness.reports).toEqual([{ source: 'window.error', type: 'Error' }]);
    // The event's own `message` is text as well: nothing here falls back to it.
    expect(JSON.stringify(harness.reports)).not.toContain('Script error.');
  });

  it('reports an unhandled rejection by type only', async () => {
    const harness = await createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    window.dispatchEvent(
      new PromiseRejectionEvent('unhandledrejection', {
        promise: Promise.resolve(),
        reason: new Error('the query failed'),
      }),
    );

    expect(harness.reports).toEqual([{ source: 'unhandledrejection', type: 'Error' }]);
    expect(JSON.stringify(harness.reports)).not.toContain('the query failed');
  });

  it('stops reporting once unregistered, and removes both listeners', async () => {
    const harness = await createHarness();
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

  it('shares one budget with the Angular handler', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-24T10:00:00Z'));
    const harness = await createHarness();
    unregister = registerGlobalErrorListeners(window, harness.service);

    for (let index = 0; index < 10; index += 1) {
      harness.handler.handleError(new Error(`angular ${index}`));
    }
    window.dispatchEvent(new ErrorEvent('error', { error: new Error('window one') }));

    expect(harness.reports).toHaveLength(10);
    expect(harness.reports.map((report) => report.source)).not.toContain('window.error');
  });
});
