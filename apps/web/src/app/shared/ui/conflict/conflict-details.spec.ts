// State sheet: docs/design/web/state-sheet.md#412-conflict-dialog
import type { ApiError } from '../../../core/http/api-error';
import { conflictFromError } from './conflict-details';

const error: ApiError = {
  kind: 'precondition_failed',
  status: 412,
  code: 'precondition_failed',
  message: 'The version changed',
  requestId: 'request-412',
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

describe('conflictFromError', () => {
  it('keys fields by the retained draft and ignores unknown detail entries', () => {
    const draft = Object.freeze({ title: '<img src=x onerror=alert(1)>', notes: '' });
    const result = conflictFromError(
      {
        ...error,
        details: [
          { path: 'If-Match', reason: 'stale' },
          { path: 'revision_id', reason: 'revision-18' },
          { path: 'unknown', reason: 'not a server value' },
          { path: 'title', reason: 'validation reason, not a field snapshot' },
        ],
      },
      draft,
      { title: 'Title', notes: 'Notes', unknown: 'Not in draft' },
    );
    expect(result).toEqual({
      currentRevision: 'revision-18',
      fields: [
        { id: 'title', label: 'Title', local: draft.title, server: null },
        { id: 'notes', label: 'Notes', local: '', server: null },
      ],
    });
    expect(draft.title).toBe('<img src=x onerror=alert(1)>');
  });

  it.each(['revision_id', 'version', 'order'])(
    'reads the documented %s metadata identifier as text',
    (path) => {
      const result = conflictFromError(
        { ...error, details: [{ path, reason: '<script>identifier</script>' }] },
        {},
        {},
      );
      expect(result).toEqual({ currentRevision: '<script>identifier</script>', fields: [] });
    },
  );

  it('does not invent server values or a revision from missing or unrelated details', () => {
    const result = conflictFromError(
      { ...error, details: [{ path: 'revision_id', reason: ' ' }, { reason: 'stale' }] },
      { title: 'draft' },
      {},
    );
    expect(result).toEqual({
      currentRevision: null,
      fields: [{ id: 'title', label: 'title', local: 'draft', server: null }],
    });
  });

  it('accepts a non-412 error without throwing or misreading validation reasons', () => {
    const result = conflictFromError(
      {
        ...error,
        kind: 'validation',
        status: 422,
        details: [{ path: 'version', reason: 'invalid' }],
      },
      { version: 'draft-version' },
      { version: 'Version' },
    );
    expect(result.currentRevision).toBeNull();
    expect(result.fields.at(0)?.server).toBeNull();
    expect(result.fields.at(0)?.local).toBe('draft-version');
  });

  it('does not use prototype properties as field labels', () => {
    expect(conflictFromError(error, { toString: 'draft' }, {}).fields[0]).toEqual({
      id: 'toString',
      label: 'toString',
      local: 'draft',
      server: null,
    });
  });
});
