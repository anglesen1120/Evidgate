// 09.1.1-T3.2 (AC5 half, T38; ADR-0008 rule 1): the cookie session authenticates mutations
// through the XSRF double-submit pair — `XSRF-TOKEN` cookie read by Angular, `X-XSRF-TOKEN`
// header sent on same-origin mutations only. A setup without `PlatformLocation` makes Angular's
// interceptor skip silently, so every case here provides the real `BrowserPlatformLocation`; the
// last test pins that, because a vacuous suite would pass without this file meaning anything.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { provideApi } from './api-client';

const COOKIE = 'XSRF-TOKEN=abc123; path=/';

describe('XSRF wiring (09.1.1-T3.2, AC5)', () => {
  let httpMock: HttpTestingController;
  let http: HttpClient;

  beforeEach(() => {
    document.cookie = COOKIE;
    TestBed.configureTestingModule({
      providers: [
        provideApi(),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    http = TestBed.inject(HttpClient);
  });

  afterEach(() => {
    httpMock.verify();
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  it('sends X-XSRF-TOKEN from the cookie on a same-origin POST', async () => {
    const response = firstValueFrom(http.post('/v1/projects/p-1/runs', { manifest_id: 'mf-1' }));
    const request = httpMock.expectOne('/v1/projects/p-1/runs');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('abc123');
    request.flush({});
    await response;
  });

  it('does not send X-XSRF-TOKEN on a GET', async () => {
    const response = firstValueFrom(http.get('/v1/projects/p-1/runs'));
    const request = httpMock.expectOne('/v1/projects/p-1/runs');
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    request.flush({ items: [], next_cursor: null });
    await response;
  });

  it('does not send X-XSRF-TOKEN to another origin, even on a POST', async () => {
    const response = firstValueFrom(http.post('https://foreign.example/collect', { a: 1 }));
    const request = httpMock.expectOne('https://foreign.example/collect');
    expect(request.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    request.flush({});
    await response;
  });

  it('runs with a real PlatformLocation, so the cookie case is not vacuous', () => {
    // Without this provider Angular's XSRF interceptor swallows the injection error and skips,
    // which would make the two negative cases above pass for the wrong reason.
    expect(TestBed.inject(PlatformLocation)).toBeInstanceOf(BrowserPlatformLocation);
    expect(document.cookie).toContain('XSRF-TOKEN=abc123');
  });
});
