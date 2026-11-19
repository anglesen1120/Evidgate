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

  it.each(['revision_id', 'version', 'order', '/revision_id', '/version', '/order'])(
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

  it.each(['/items/0/version', '/version/0', '//version', '/If-Match', '/unknown'])(
    'does not treat the pointer %s as concurrency metadata',
    (path) => {
      const result = conflictFromError(
        { ...error, details: [{ path, reason: 'not a revision' }] },
        { title: 'draft' },
        {},
      );
      expect(result.currentRevision).toBeNull();
    },
  );

  it('reads an authoring stale body in the server pointer form', () => {
    const result = conflictFromError(
      {
        ...error,
        details: [
          { path: '/If-Match', reason: 'stale' },
          { path: '/version', reason: '7' },
        ],
      },
      { title: 'draft' },
      { title: 'Title' },
    );
    expect(result.currentRevision).toBe('7');
  });

  it.each([
    { path: '/version', id: 'version', label: 'Version', reason: '7' },
    { path: '/order', id: 'order', label: 'Sibling order', reason: 'server-order-hash' },
    { path: '/revision_id', id: 'revision_id', label: 'Revision', reason: 'revision-18' },
    { path: 'version', id: 'version', label: 'Version', reason: '7' },
    { path: 'order', id: 'order', label: 'Sibling order', reason: 'server-order-hash' },
    { path: 'revision_id', id: 'revision_id', label: 'Revision', reason: 'revision-18' },
  ])('maps $path to its labelled draft field and server value', ({ path, id, label, reason }) => {
    const draft = Object.freeze({ [id]: 'retained draft', title: 'Uncompared title' });
    const result = conflictFromError({ ...error, details: [{ path, reason }] }, draft, {
      [id]: label,
      title: 'Title',
    });

    expect(result).toEqual({
      currentRevision: reason,
      fields: [
        { id, label, local: 'retained draft', server: reason },
        { id: 'title', label: 'Title', local: 'Uncompared title', server: null },
      ],
    });
    expect(draft[id]).toBe('retained draft');
  });

  it('associates each metadata field with its own detail rather than the first revision', () => {
    const result = conflictFromError(
      {
        ...error,
        details: [
          { path: '/version', reason: '7' },
          { path: 'order', reason: 'server-order-hash' },
          { path: '/revision_id', reason: 'revision-18' },
        ],
      },
      { version: '6', order: 'draft-order-hash', revision_id: 'revision-17' },
      { version: 'Version', order: 'Sibling order', revision_id: 'Revision' },
    );

    expect(result).toEqual({
      currentRevision: '7',
      fields: [
        { id: 'version', label: 'Version', local: '6', server: '7' },
        {
          id: 'order',
          label: 'Sibling order',
          local: 'draft-order-hash',
          server: 'server-order-hash',
        },
        { id: 'revision_id', label: 'Revision', local: 'revision-17', server: 'revision-18' },
      ],
    });
  });

  it.each(['/unknown', '/items/0/version', '/items/0/order', '/items/0/revision_id'])(
    'keeps generic uncompared fields for the unrecognised pointer %s',
    (path) => {
      const result = conflictFromError(
        { ...error, details: [{ path, reason: 'not a server value' }] },
        { version: '6', order: 'draft-order', revision_id: 'revision-17', unknown: 'draft' },
        { version: 'Version', order: 'Sibling order', revision_id: 'Revision', unknown: 'Other' },
      );

      expect(result).toEqual({
        currentRevision: null,
        fields: [
          { id: 'version', label: 'Version', local: '6', server: null },
          { id: 'order', label: 'Sibling order', local: 'draft-order', server: null },
          { id: 'revision_id', label: 'Revision', local: 'revision-17', server: null },
          { id: 'unknown', label: 'Other', local: 'draft', server: null },
        ],
      });
    },
  );

  it.each(['', ' '])('does not treat a blank metadata reason %j as a server value', (reason) => {
    const result = conflictFromError(
      { ...error, details: [{ path: '/version', reason }] },
      { version: '6' },
      { version: 'Version' },
    );

    expect(result).toEqual({
      currentRevision: null,
      fields: [{ id: 'version', label: 'Version', local: '6', server: null }],
    });
  });
});
