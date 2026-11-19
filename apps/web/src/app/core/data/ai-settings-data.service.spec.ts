import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AiSettingsView } from '@evidgate/sdk';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { isApiRequestError } from '../http/api-error';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { AiSettingsDataService } from './ai-settings-data.service';
import head from '../../../../../../fixtures/web-api/ai-settings-head/input.json';

const baseUrl = location.origin + API_BASE_PATH;
const url = baseUrl + '/projects/p1/ai-settings';
describe('AiSettingsDataService', () => {
  let http: HttpTestingController;
  let data: AiSettingsDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(baseUrl),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    data = TestBed.inject(AiSettingsDataService);
  });
  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });
  it('sends the loaded ETag and one explicit intent key when saving', async () => {
    const read = data.read('p1');
    (await takeRequest(http, url)).flush(JSON.stringify(head.body), {
      headers: { ...JSON_HEADERS, ETag: '"2"' },
    });
    const loaded = await read;
    const save = data.save('p1', { settings: loaded.view.settings }, loaded.etag, 'intent-one');
    const request = await takeRequest(http, url);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('If-Match')).toBe('"2"');
    expect(request.request.headers.get('Idempotency-Key')).toBe('intent-one');
    request.flush(JSON.stringify(head.body), {
      status: 201,
      statusText: 'Created',
      headers: { ...JSON_HEADERS, ETag: '"3"' },
    });
    expect((await save).appended).toBe(true);
  });
  for (const [status, kind, details] of [
    [412, 'precondition_failed', [{ path: '/version_no', reason: '3' }]],
    [
      422,
      'validation',
      [{ path: '/settings/writing_style', reason: 'max_length', limit: 250, actual: 251 }],
    ],
  ] as const) {
    it(
      'preserves typed ' + status + ' field pointers without treating them as a snapshot',
      async () => {
        const pending = data
          .save('p1', { settings: (head.body as AiSettingsView).settings }, '"2"', 'intent')
          .catch((error: unknown) => error);
        (await takeRequest(http, url)).flush(
          JSON.stringify({
            error: {
              code: kind,
              message: 'Refused',
              request_id: '018f0000-0000-7000-8000-00000000b441',
              retryable: false,
              details,
            },
          }),
          { status, statusText: 'Refused', headers: JSON_HEADERS },
        );
        const error = await pending;
        expect(isApiRequestError(error)).toBe(true);
        if (isApiRequestError(error)) {
          expect(error.error.kind).toBe(kind);
          expect(error.error.details).toEqual(details);
        }
      },
    );
  }
  for (const operation of ['read', 'versions', 'version', 'templates'] as const) {
    it('forwards abort for ' + operation, async () => {
      const controller = new AbortController();
      const pending = (
        operation === 'read'
          ? data.read('p1', controller.signal)
          : operation === 'versions'
            ? data.versions('p1', {}, controller.signal)
            : operation === 'version'
              ? data.version('p1', 1, controller.signal)
              : data.templates('p1', controller.signal)
      ).catch((error: unknown) => error);
      const request = await takeRequest(http, url);
      controller.abort();
      expect(request.cancelled).toBe(true);
      expect(await pending).toBeInstanceOf(DOMException);
    });
  }
});
