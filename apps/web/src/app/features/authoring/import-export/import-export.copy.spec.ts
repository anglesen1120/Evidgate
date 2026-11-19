import permissions from '../../../../../../../contracts/permissions.json';
import type { ApiError } from '../../../core/http/api-error';
import {
  AUTHORING_READ,
  AUTHORING_WRITE,
  IMPEX_COPY,
  importFailure,
  importReason,
  safeImportPath,
  importFieldLabel,
  importProblemTarget,
} from './import-export.copy';

describe('authoring import/export typed refusal presentation', () => {
  it('pins the scoped permission constants to the contract catalogue without bundling its prose', () => {
    const codes = permissions.permissions.map((permission) => permission.code);
    expect(codes).toContain(AUTHORING_READ);
    expect(codes).toContain(AUTHORING_WRITE);
    expect([AUTHORING_READ, AUTHORING_WRITE]).toEqual(['authoring:read', 'authoring:write']);
  });
  for (const [reason, text] of [
    ['unknown_parent', 'earlier node'],
    ['orphan_row', 'owning item or node'],
    ['duplicate_key', 'All rows are retained'],
    ['code_conflict', "project's prefix"],
    ['value_invalid', 'import contract'],
    ['steps_invalid', 'paired action'],
    ['invalid_text', "server's text requirements"],
    ['unsupported_format', 'CSV header'],
  ]) {
    it(`maps 422 ${reason} to a typed, actionable message without refused cell text`, () => {
      expect(importReason(reason)).toContain(text);
      expect(importReason(reason)).toContain(`(${reason})`);
    });
  }
  const base: ApiError = {
    kind: 'unexpected',
    status: 0,
    code: 'unexpected',
    message: 'REFUSED PRIVATE CELL',
    requestId: null,
    retryable: false,
    retryAfterSeconds: null,
    details: [],
  };
  for (const [kind, text] of [
    ['payload_too_large', '5 MiB or 5,000'],
    ['validation', 'Remaining rows are unverified'],
    ['idempotency_conflict', 'different request'],
    ['forbidden', 'permission'],
    ['rate_limited', 'Retry-After'],
    ['unavailable', 'Retry-After'],
    ['network', 'outcome is unconfirmed'],
  ] as const) {
    it(`maps ${kind} without echoing the error envelope message`, () => {
      expect(importFailure({ ...base, kind })).toContain(text);
      expect(importFailure({ ...base, kind })).not.toContain(base.message);
    });
  }
  it('maps export 413 to a smaller explicit scope, never a truncated or partial file', () => {
    expect(importFailure({ ...base, kind: 'payload_too_large' }, true)).toBe(IMPEX_COPY.exportCap);
  });
  it('never renders unrecognised reason or path text from an error as cell content', () => {
    expect(importReason('REFUSED PRIVATE CELL')).not.toContain('REFUSED PRIVATE CELL');
    expect(safeImportPath('/REFUSED PRIVATE CELL')).toBe('/file');
    expect(safeImportPath('/header')).toBe('/header');
    expect(safeImportPath('/steps')).toBe('/steps');
  });
  it('labels field-specific error links without presenting raw wire paths as localized labels', () => {
    expect(importFieldLabel('/title')).toBe('Title');
    expect(importFieldLabel('/steps')).toBe('Action and expected-result steps');
    expect(importProblemTarget(2, '/steps')).toBe('import-row-2-steps');
    expect(importProblemTarget(2, '/PRIVATE REFUSED CELL')).toBe('import-row-2-status');
    expect(importFieldLabel('/PRIVATE REFUSED CELL')).toBe(IMPEX_COPY.file);
  });
});
