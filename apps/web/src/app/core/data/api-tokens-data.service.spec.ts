import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideApi } from '../http/api-client';
import { createIntent } from '../http/intent';
import { ApiTokensService } from './api-tokens-data.service';

describe('ApiTokensService generated operations', () => {
  let service: ApiTokensService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideApi(`${location.origin}/v1`),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    service = TestBed.inject(ApiTokensService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  async function take(method: string, path: string): Promise<TestRequest> {
    let found: TestRequest | undefined;
    await vi.waitFor(() => {
      const requests = http.match(
        (request) => request.method === method && new URL(request.url).pathname.endsWith(path),
      );
      found = requests[0];
      expect(found).toBeDefined();
    });
    if (!found) throw new Error('Generated request did not arrive');
    return found;
  }

  it('calls the generated list with a bounded page size and opaque cursor', async () => {
    const pending = service.list('p-1', 'opaque-next');
    const request = await take('GET', '/projects/p-1/api-tokens');
    expect(new URL(request.request.urlWithParams).searchParams.get('limit')).toBe('50');
    expect(new URL(request.request.urlWithParams).searchParams.get('cursor')).toBe('opaque-next');
    request.flush(JSON.stringify({ items: [], next_cursor: null }), {
      headers: { 'Content-Type': 'application/json' },
    });
    await expect(pending).resolves.toEqual({ items: [], next_cursor: null });
  });

  it('returns the create answer directly and uses the generated revoke path', async () => {
    const body = { name: 'Reader', permissions: ['run:read'], expires_at: '2027-01-01T00:00:00Z' };
    const intent = createIntent();
    const creating = service.create('p-1', body, intent);
    const request = await take('POST', '/projects/p-1/api-tokens');
    expect(request.request.body).toBe(JSON.stringify(body));
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    const answer = { ...body, id: 'a-1', prefix: 'public-part', token: 'one-time-fixture' };
    request.flush(JSON.stringify(answer), { headers: { 'Content-Type': 'application/json' } });
    await expect(creating).resolves.toEqual(answer);
    const revoking = service.revoke('a-1', intent);
    (await take('DELETE', '/api-tokens/a-1')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    await expect(revoking).resolves.toBeUndefined();
  });
});
