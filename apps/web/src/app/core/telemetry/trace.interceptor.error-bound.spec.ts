// The 64 KiB bound on a string error body is exact UTF-8 bytes (it bounds the input parsed for
// span enrichment):
// at the bound the envelope code is read, one byte over it is skipped, and the call's result
// (the rethrown error) is the same either way because tracing fails open.
import { HttpErrorResponse, HttpHeaders, HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, NavigationStart, Router, type Event as RouterEvent } from '@angular/router';
import { InMemorySpanExporter } from '@opentelemetry/sdk-trace';
import { Subject, firstValueFrom, throwError } from 'rxjs';

import { startTelemetrySdk } from './telemetry-sdk';
import type { TelemetrySdkOptions } from './telemetry-sdk';
import type { TelemetryConfig } from './telemetry-config';
import { TELEMETRY_SDK_LOADER, TelemetryService } from './telemetry.service';
import { telemetryInterceptor } from './trace.interceptor';

const BOUND = 64 * 1024;
const encoder = new TextEncoder();

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

async function codeOf(body: string): Promise<unknown> {
  const exporter = await harness();
  const error = new HttpErrorResponse({ status: 422, error: body, headers: new HttpHeaders() });
  const request = new HttpRequest('GET', '/v1/projects/p/authoring/export');
  const events = throwError(() => error);
  await expect(
    firstValueFrom(
      TestBed.runInInjectionContext(() => telemetryInterceptor(request, () => events)),
    ),
  ).rejects.toBe(error);
  const span = exporter.getFinishedSpans().find((candidate) => candidate.name === 'api.request');
  if (span === undefined) {
    throw new Error('expected an api.request span');
  }
  return span.attributes['evidgate.api.error_code'];
}

// An envelope whose padding is `fill` repeated until the whole text is exactly `bytes` UTF-8 bytes;
// `fill` is one character of `width` bytes, ASCII pads the remainder.
function envelopeOfBytes(bytes: number, fill: string): string {
  const width = encoder.encode(fill).length;
  const head = '{"error":{"code":"invalid_workbook","pad":"';
  const foot = '"}}';
  const room = bytes - encoder.encode(head + foot).length;
  const text = head + fill.repeat(Math.floor(room / width)) + 'a'.repeat(room % width) + foot;
  expect(encoder.encode(text).length).toBe(bytes);
  return text;
}

describe.each([
  ['ASCII', 'a'],
  ['2-byte', 'é'],
  ['3-byte CJK', '日'],
  ['surrogate pair', '😀'],
])('traced error bound, %s text', (_name, fill) => {
  it('enriches the span at exactly the bound', async () => {
    await expect(codeOf(envelopeOfBytes(BOUND, fill))).resolves.toBe('invalid_workbook');
  });

  it('skips enrichment one byte over the bound and still rethrows the same error', async () => {
    await expect(codeOf(envelopeOfBytes(BOUND + 1, fill))).resolves.toBeUndefined();
  });
});
