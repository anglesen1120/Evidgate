import { clearTranslations, loadTranslations } from '@angular/localize';
import type { ErrorEnvelope } from '@evidgate/sdk';

import { mapApiError, mapNetworkError, mapTimeoutError } from './error-mapper';

const cases = [
  {
    id: 'core.http.error.network',
    map: mapNetworkError,
    en: 'The request did not reach the API; no response was received.',
    vi: 'Yêu cầu không đến được API; không nhận được phản hồi.',
    code: 'network',
  },
  {
    id: 'core.http.error.timeout',
    map: mapTimeoutError,
    en: 'The API did not answer within the request deadline; the request may or may not have reached the server.',
    vi: 'API không phản hồi trong thời hạn yêu cầu; yêu cầu có thể đã đến hoặc chưa đến máy chủ.',
    code: 'request_timeout',
  },
  {
    id: 'core.http.error.unexpected-status',
    map: () => mapApiError({ status: 204, body: null }),
    en: 'A success status reached the error mapper; read it through the API gateway.',
    vi: 'Mã trạng thái thành công đã được chuyển đến bộ ánh xạ lỗi; hãy đọc phản hồi qua cổng API.',
    code: 'unexpected_status',
  },
  {
    id: 'core.http.error.envelope-missing',
    map: () => mapApiError({ status: 503, body: null }),
    en: 'The API answered 503 without the 00 §9.2 error envelope.',
    vi: 'API trả về mã trạng thái 503 mà không có phần lỗi theo mục 00 §9.2.',
    code: 'envelope_missing',
  },
] as const;

const translations = {
  'core.http.error.network': cases[0].vi,
  'core.http.error.timeout': cases[1].vi,
  'core.http.error.unexpected-status': cases[2].vi,
  'core.http.error.envelope-missing':
    'API trả về mã trạng thái {$STATUS} mà không có phần lỗi theo mục 00 §9.2.',
};

describe('client-generated API error messages', () => {
  beforeEach(() => clearTranslations());
  afterEach(() => clearTranslations());

  it.each(cases)(
    'retains the English source for $id without a translation',
    ({ map, en, code }) => {
      const error = map();
      expect(error.message).toBe(en);
      expect(error.code).toBe(code);
    },
  );

  it.each(cases)('localizes $id without changing technical metadata', ({ map, vi }) => {
    const original = map();
    loadTranslations(translations);
    const localized = map();
    expect(localized.message).toBe(vi);
    expect({ ...localized, message: original.message }).toEqual(original);
  });

  it.each([0, -1])('localizes a no-response status %i through the public mapper', (status) => {
    loadTranslations(translations);
    const error = mapApiError({ status, body: null });
    expect(error.message).toBe(cases[0].vi);
    expect(error.kind).toBe('network');
    expect(error.code).toBe('network');
    expect(error.status).toBe(0);
  });

  it.each([199, 300, 503])(
    'preserves status %i in a localized missing-envelope message',
    (status) => {
      loadTranslations(translations);
      const error = mapApiError({ status, body: '<html>untrusted proxy response</html>' });
      expect(error.message).toBe(
        `API trả về mã trạng thái ${status} mà không có phần lỗi theo mục 00 §9.2.`,
      );
      expect(error.code).toBe('envelope_missing');
      expect(error.status).toBe(status);
      expect(error.message).not.toContain('untrusted');
    },
  );

  it('preserves the server message and details even when they match client source copy', () => {
    loadTranslations(translations);
    const details = [
      { field: 'scope', reason: 'Server-owned explanation <strong>unchanged</strong>' },
    ];
    const body: ErrorEnvelope = {
      error: {
        code: 'network',
        message: cases[0].en,
        request_id: 'request-from-server',
        retryable: true,
        details,
      },
    };
    const error = mapApiError({ status: 422, body });
    expect(error.message).toBe(cases[0].en);
    expect(error.code).toBe('network');
    expect(error.requestId).toBe('request-from-server');
    expect(error.details).toBe(details);
    expect(error.details).toEqual(body.error.details);
    expect(error.retryable).toBe(true);
  });
});
