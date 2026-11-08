// The usage summary read, against the real SDK -> adapter -> HttpClient stack.
//
// Two facts here are load-bearing for the badge: the request is the org's summary address with no
// query, and a refusal of THIS read is never reported as a quota signal — otherwise the reload a
// quota signal triggers would ask for the read that just failed. The control case proves the same
// envelope is reported when the read does not opt out, so the difference is the option and not the
// body.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { UsageSummaryDto } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { ApiGateway } from '../http/api-gateway';
import { QuotaExceededRelay } from '../http/quota-exceeded-relay';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { QuotaDataService } from './quota-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const SUMMARY_URL = `${API_BASE_URL}/orgs/o-1/usage/summary`;

const SUMMARY: UsageSummaryDto = {
  period: '2026-11',
  project: null,
  metrics: [
    {
      metric: 'egress_gb',
      scope: 'tenant',
      used: null,
      limit: null,
      warned_at: null,
      blocked_at: null,
      retry_after_seconds: null,
      unavailable_reason: 'storage_usage_daily.egress_bytes has no rows for this period',
      projects: [],
      projects_status: { blocked_count: 0, warned_count: 0 },
    },
  ],
  next_cursor: null,
};

const QUOTA_EXCEEDED = {
  error: {
    code: 'quota_exceeded',
    message: 'The billing period has no results left.',
    request_id: 'req-quota-1',
    retryable: true,
  },
};

/** The rejection one promise settles with, so a refusal can be inspected instead of thrown. */
async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

describe('QuotaDataService', () => {
  let httpMock: HttpTestingController;
  let service: QuotaDataService;
  let reports: number;

  beforeEach(() => {
    reports = 0;
    TestBed.configureTestingModule({
      providers: [
        provideApi(API_BASE_URL),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    TestBed.inject(QuotaExceededRelay).register(() => {
      reports += 1;
    });
    httpMock = TestBed.inject(HttpTestingController);
    service = TestBed.inject(QuotaDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('summary() reads the org summary address and resolves the body as served', async () => {
    const read = service.summary('o-1');
    const request = await takeRequest(httpMock, SUMMARY_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.url).toBe(SUMMARY_URL);
    expect(new URL(request.request.urlWithParams).search).toBe('');

    request.flush(JSON.stringify(SUMMARY), {
      status: 200,
      statusText: '',
      headers: JSON_HEADERS,
    });
    await expect(read).resolves.toEqual(SUMMARY);
    expect(reports).toBe(0);
  });

  it('summary() rejects a 403 with the typed forbidden failure and reports nothing', async () => {
    const forbidden = {
      error: {
        code: 'forbidden',
        message: 'The caller may not read this org usage.',
        request_id: 'req-quota-2',
        retryable: false,
      },
    };
    const read = service.summary('o-1');
    const request = await takeRequest(httpMock, SUMMARY_URL);
    request.flush(JSON.stringify(forbidden), {
      status: 403,
      statusText: '',
      headers: JSON_HEADERS,
    });

    const rejection = await rejectionOf(read);
    if (!isApiRequestError(rejection)) {
      throw rejection;
    }
    expect(rejection.error.kind).toBe('forbidden');
    expect(rejection.error.requestId).toBe('req-quota-2');
    expect(reports).toBe(0);
  });

  it('summary() never reports its own 429 quota_exceeded, while the same answer elsewhere does', async () => {
    const read = service.summary('o-1');
    const request = await takeRequest(httpMock, SUMMARY_URL);
    request.flush(JSON.stringify(QUOTA_EXCEEDED), {
      status: 429,
      statusText: '',
      headers: { ...JSON_HEADERS, 'Retry-After': '60' },
    });
    await rejectionOf(read);
    expect(reports).toBe(0);

    // The control: the identical envelope through a call that did not opt out is reported once.
    const gateway = TestBed.inject(ApiGateway);
    const other = gateway.call(() =>
      Promise.resolve({
        response: new Response(JSON.stringify(QUOTA_EXCEEDED), {
          status: 429,
          headers: { ...JSON_HEADERS, 'Retry-After': '60' },
        }),
        error: QUOTA_EXCEEDED,
      }),
    );
    await rejectionOf(other);
    expect(reports).toBe(1);
  });

  it('summary() cancels the read when the caller aborts it', async () => {
    const controller = new AbortController();
    const read = service.summary('o-1', controller.signal);
    await takeRequest(httpMock, SUMMARY_URL);

    controller.abort();

    const rejection = await rejectionOf(read);
    expect(rejection).toBeInstanceOf(DOMException);
    expect((rejection as DOMException).name).toBe('AbortError');
  });
});
