import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { MembersDataService } from './members-data.service';

const BASE = `${location.origin}${API_BASE_PATH}`;

describe('MembersDataService page size', () => {
  let http: HttpTestingController;
  let service: MembersDataService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(BASE),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(MembersDataService);
  });
  afterEach(() => http.verify());

  it.each([undefined, 20, 50, 100])(
    'sends the selected limit %s with the opaque cursor',
    async (limit) => {
      const result = service.listMemberships('org-1', 'opaque+/cursor=', limit);
      const request = await takeRequest(http, `${BASE}/orgs/org-1/memberships`);
      const url = new URL(request.request.urlWithParams);
      expect(url.searchParams.get('limit')).toBe(String(limit ?? 20));
      expect(url.searchParams.get('cursor')).toBe('opaque+/cursor=');
      request.flush({ items: [], next_cursor: null }, { headers: JSON_HEADERS });
      await expect(result).resolves.toEqual({ items: [], next_cursor: null });
    },
  );
});
