// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: client error reporting with
// PII/secret scrubbing): the two ways a browser app learns that something failed — Angular's
// `ErrorHandler` for anything inside the framework, and the window's `error` /
// `unhandledrejection` for anything outside it.
//
// Every report is an id and a classification, never a payload: an API failure contributes its
// status, kind, code, retryable flag and request id and never its message (server text is not the
// client's to export), and a thrown error contributes its source and its constructor name — no
// message text at all (D-S69-Q2, 00 §9.1 "ids only"), not even a scrubbed line. The scrubbing
// itself happens in the exporter (`scrubbing-exporter.ts`), so nothing here can leak a value that
// the allow-list does not carry — this file only avoids *offering* one.
//
// Reports are rate limited: an error inside a render loop can fire hundreds of times a second,
// and the point of a client error span is to be found in a trace, not to be the outage. The
// budget is per service instance (ten per rolling minute), shared by the Angular handler and the
// global listeners so the two cannot double the ceiling, and a dropped report is dropped
// silently — logging the drop would be the same flood one level down. A report is only counted
// when the facade has a loaded runtime: while the chunk loads (or after it failed) a report
// cannot be exported, so it must not spend a slot the first exportable report would need.
import { ErrorHandler, inject, Injectable } from '@angular/core';

import { isApiRequestError } from '../http/api-error';
import { TelemetryService } from './telemetry.service';
import type { ErrorReport } from './telemetry-sdk';

/** Reports allowed inside one window, shared by every source. */
const MAX_REPORTS = 10;

/** The rolling window, in milliseconds. */
const WINDOW_MS = 60_000;

/**
 * The report timestamps of each service instance. A `WeakMap` keyed by the instance is both the
 * budget and its own reset: the application has one root service, and a fresh `TestBed` gets a
 * fresh budget without any test-only reset hook to forget.
 */
const budgets = new WeakMap<TelemetryService, number[]>();

/**
 * Whether `service` may report now; records the report when it may.
 *
 * A null runtime (the chunk is loading, or the load failed) is checked first and costs no budget:
 * `reportError` would be a no-op, so charging a slot for it would spend the minute's allowance on
 * reports nobody will ever see and drop the eleventh — the first one that could have been
 * exported. The budget is the exporter's, so it is only spent when there is an exporter.
 */
function withinBudget(service: TelemetryService, now: number): boolean {
  if (service.runtime === null) {
    return false;
  }
  const kept = (budgets.get(service) ?? []).filter((at) => at > now - WINDOW_MS);
  if (kept.length >= MAX_REPORTS) {
    budgets.set(service, kept);
    return false;
  }
  kept.push(now);
  budgets.set(service, kept);
  return true;
}

/** The error's constructor name (`TypeError`, `ApiRequestError`, …), or `Error`. */
function errorType(error: unknown): string {
  return error instanceof Error && error.name !== '' ? error.name : 'Error';
}

/** The report of an `ApiRequestError`: identifiers only, no message text. */
function reportOfApiError(error: unknown): ErrorReport | null {
  if (!isApiRequestError(error)) {
    return null;
  }
  const api = error.error;
  const report: ErrorReport = {
    source: 'api',
    type: error.name,
    status: api.status,
    errorKind: api.kind,
    retryable: api.retryable,
  };
  if (api.code !== '') {
    report.errorCode = api.code;
  }
  if (api.requestId !== null) {
    report.requestId = api.requestId;
  }
  return report;
}

/**
 * Replaces Angular's `ErrorHandler`. It keeps Angular's own console output — a developer must
 * still see the error in the console — and then records the span, if the minute's budget allows.
 */
@Injectable()
export class TelemetryErrorHandler implements ErrorHandler {
  private readonly delegate = new ErrorHandler();
  private readonly service = inject(TelemetryService);

  /** @param error whatever Angular caught; a rejection or a thrown string included. */
  handleError(error: unknown): void {
    this.delegate.handleError(error);
    if (!withinBudget(this.service, Date.now())) {
      return;
    }
    const apiReport = reportOfApiError(error);
    if (apiReport !== null) {
      this.service.reportError(apiReport);
      return;
    }
    this.service.reportError({ source: 'angular', type: errorType(error) });
  }
}

/**
 * Listens for the two global failures Angular cannot see: an exception outside the framework's
 * call path (`error`) and an unhandled promise (`unhandledrejection`). Returns the function that
 * unregisters both; a caller that keeps the app alive across tests or an HMR reload uses it.
 */
export function registerGlobalErrorListeners(win: Window, service: TelemetryService): () => void {
  const onError = (event: ErrorEvent): void => {
    if (!withinBudget(service, Date.now())) {
      return;
    }
    // `event.error` only: the event's `message` is text, and no report carries text.
    service.reportError({ source: 'window.error', type: errorType(event.error) });
  };
  const onUnhandledRejection = (event: PromiseRejectionEvent): void => {
    if (!withinBudget(service, Date.now())) {
      return;
    }
    service.reportError({ source: 'unhandledrejection', type: errorType(event.reason) });
  };
  win.addEventListener('error', onError);
  win.addEventListener('unhandledrejection', onUnhandledRejection);
  return () => {
    win.removeEventListener('error', onError);
    win.removeEventListener('unhandledrejection', onUnhandledRejection);
  };
}
