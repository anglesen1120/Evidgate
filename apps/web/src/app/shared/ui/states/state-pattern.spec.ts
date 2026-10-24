// State sheet: docs/design/web/state-sheet.md#403-pattern
// State sheet: docs/design/web/state-sheet.md#404-pattern
import type { ApiError, ApiErrorKind } from '../../../core/http/api-error';
import { patternForError } from './state-pattern';

const cases = {
  unauthenticated: 'error',
  csrf_failed: 'error',
  forbidden: 'forbidden',
  not_found: 'not_found',
  idempotency_conflict: 'error',
  conflict: 'error',
  precondition_failed: 'error',
  payload_too_large: 'error',
  validation: 'error',
  rate_limited: 'error',
  unavailable: 'error',
  network: 'error',
  timeout: 'error',
  unexpected: 'error',
} as const satisfies Record<ApiErrorKind, 'forbidden' | 'not_found' | 'error'>;

function errorOfKind(kind: ApiErrorKind): ApiError {
  return {
    kind,
    // Deliberately misleading status/code: routing must use only the typed kind.
    status: kind === 'forbidden' ? 404 : 403,
    code: kind === 'not_found' ? 'forbidden' : 'not_found',
    message: 'Untrusted <script>resource name</script>',
    requestId: 'request-001',
    retryable: false,
    retryAfterSeconds: null,
    details: [],
  };
}

describe('patternForError', () => {
  for (const kind of Object.keys(cases) as ApiErrorKind[]) {
    it(`maps ${kind} to ${cases[kind]} independently of HTTP status and code`, () => {
      const error = errorOfKind(kind);
      const original = { ...error };
      Object.freeze(error);
      expect(patternForError(error)).toBe(cases[kind]);
      expect(error).toEqual(original);
    });
  }

  it('uses the generic error pattern when no failure exists', () => {
    expect(patternForError(null)).toBe('error');
  });
});
