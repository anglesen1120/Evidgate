// The span of a byte-reply call and the bound on a string error body, driven through the
// interceptor directly like `trace.interceptor.spec.ts` (HttpClient is outside this directory).
import { HttpErrorResponse, HttpHeaders, HttpRequest } from '@angular/common/http';
import type { HttpEvent } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, NavigationStart, Router, type Event as RouterEvent } from '@angular/router';
import { InMemorySpanExporter } from '@opentelemetry/sdk-trace';
import { Subject, firstValueFrom, of, throwError, type Observable } from 'rxjs';

import { BINARY_BODY_LIMIT_BYTES } from '../http/binary-transport';
import { startTelemetrySdk } from './telemetry-sdk';
import type { TelemetrySdkOptions } from './telemetry-sdk';
import type { TelemetryConfig } from './telemetry-config';
import { TELEMETRY_SDK_LOADER, TelemetryService } from './telemetry.service';
import { telemetryInterceptor } from './trace.interceptor';

const API_URL = '/v1/projects/p/authoring/export';
const RESPONSE = 4;
const DOWNLOAD_PROGRESS = 3;
const RESPONSE_HEADER = 2;

class RouterDouble {
  readonly events = new Subject<RouterEvent>();
  readonly routerState = { snapshot: { root: { routeConfig: { path: '' }, firstChild: null } } };
}

async function harness(): Promise<InMemorySpanExporter> {
  const exporter = new InMemorySpanExporter();
  const router = new RouterDouble();
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: Router, useValue: router },
      {
        provide: TELEMETRY_SDK_LOADER,
        useValue: () =>
          Promise.resolve({
            startTelemetrySdk: (config: TelemetryConfig, sdk?: TelemetrySdkOptions) =>
              startTelemetrySdk(config, { ...sdk, exporter }),
          }),
      },
    ],
  });
  TestBed.inject(TelemetryService).start();
  await Promise.resolve();
  await Promise.resolve();
  router.events.next(new NavigationStart(1, '/'));
  router.events.next(new NavigationEnd(2, '/', '/'));
  return exporter;
}

function send(
  request: HttpRequest<unknown>,
  events: Observable<HttpEvent<unknown>>,
): Observable<HttpEvent<unknown>> {
  return TestBed.runInInjectionContext(() => telemetryInterceptor(request, () => events));
}

function event(type: number, fields: Record<string, unknown>): HttpEvent<unknown> {
  return {
    type,
    status: 200,
    statusText: '',
    url: null,
    ok: true,
    headers: new HttpHeaders(),
    body: null,
    ...fields,
  };
}

function apiSpan(exporter: InMemorySpanExporter) {
  const span = exporter.getFinishedSpans().find((candidate) => candidate.name === 'api.request');
  if (span === undefined) {
    throw new Error('expected an api.request span');
  }
  return span;
}

const bytesRequest = () => new HttpRequest('GET', API_URL, { responseType: 'arraybuffer' });

describe('telemetryInterceptor: byte replies', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('records a final byte reply over the cap as a failed network call, not an ok span', async () => {
    const exporter = await harness();
    const body = new ArrayBuffer(BINARY_BODY_LIMIT_BYTES + 1);

    await firstValueFrom(send(bytesRequest(), of(event(RESPONSE, { body }))));

    const span = apiSpan(exporter);
    expect(span.attributes['evidgate.outcome']).toBe('error');
    expect(span.attributes['evidgate.api.error_kind']).toBe('network');
  });

  it('records an over-cap Content-Length and over-cap progress as failed, not cancelled', async () => {
    const exporter = await harness();
    const headers = new HttpHeaders({ 'Content-Length': String(BINARY_BODY_LIMIT_BYTES + 1) });
    const over = of(event(RESPONSE_HEADER, { headers }));
    await firstValueFrom(send(bytesRequest(), over));
    expect(apiSpan(exporter).attributes['evidgate.outcome']).toBe('error');

    exporter.reset();
    const progress = of(event(DOWNLOAD_PROGRESS, { loaded: BINARY_BODY_LIMIT_BYTES + 1 }));
    await firstValueFrom(send(bytesRequest(), progress));
    expect(apiSpan(exporter).attributes['evidgate.outcome']).toBe('error');
  });

  it('keeps a byte reply at the cap and an over-size text reply as they were', async () => {
    const exporter = await harness();
    await firstValueFrom(
      send(bytesRequest(), of(event(RESPONSE, { body: new ArrayBuffer(BINARY_BODY_LIMIT_BYTES) }))),
    );
    expect(apiSpan(exporter).attributes['evidgate.outcome']).toBe('ok');

    exporter.reset();
    const text = new HttpRequest('GET', API_URL);
    const big = new HttpHeaders({ 'Content-Length': String(BINARY_BODY_LIMIT_BYTES + 1) });
    await firstValueFrom(send(text, of(event(RESPONSE, { headers: big, body: 'x' }))));
    expect(apiSpan(exporter).attributes['evidgate.outcome']).toBe('ok');
  });
});

describe('telemetryInterceptor: string error body bound', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function codeOf(body: string): Promise<unknown> {
    const exporter = await harness();
    const error = new HttpErrorResponse({ status: 422, error: body });
    await expect(
      firstValueFrom(
        send(
          new HttpRequest('GET', API_URL),
          throwError(() => error),
        ),
      ),
    ).rejects.toBe(error);
    return apiSpan(exporter).attributes['evidgate.api.error_code'];
  }

  it('parses a string envelope within 64 KiB', async () => {
    await expect(codeOf(JSON.stringify({ error: { code: 'invalid_workbook' } }))).resolves.toBe(
      'invalid_workbook',
    );
  });

  it('does not parse a string body over 64 KiB', async () => {
    const huge = JSON.stringify({ error: { code: 'invalid_workbook', pad: 'x'.repeat(70_000) } });
    await expect(codeOf(huge)).resolves.toBeUndefined();
  });

  it('measures the bound in UTF-8 bytes, like the binary decode', async () => {
    // 30,000 three-byte characters are 90,000 bytes though only 30,000 UTF-16 units.
    const wide = JSON.stringify({ error: { code: 'invalid_workbook', pad: '€'.repeat(30_000) } });
    await expect(codeOf(wide)).resolves.toBeUndefined();
  });
});
