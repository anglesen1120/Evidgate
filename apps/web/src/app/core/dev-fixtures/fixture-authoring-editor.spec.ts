// The manual-case editor's review states: the corpus rows are each validated against the
// operations they answer, and the scenario is pinned for the transitions a static document cannot
// carry — a save, a stale tag, an invalid body, a restore and the keyset walk.
import { HttpHeaders, HttpRequest } from '@angular/common/http';
import type {
  AuthoringRestoreResponse,
  AuthoringVersionListResponse,
  ErrorEnvelope,
  ManualCaseDetailDto,
} from '@evidgate/sdk';

import {
  EDITOR_INVALID_CASE,
  EDITOR_SAVED_CASE,
  EDITOR_STALE_CASE,
  AuthoringEditorFixtureScenario,
  authoringEditorPermissions,
} from './fixture-authoring-editor';
import { FIXTURE_CATALOG_AUTHORING_EDITOR_ROWS } from './fixture-catalog-authoring-editor-rows';
import { VALIDATOR } from './fixture-contract.support';
import { FIXTURE_ROUTES } from './fixture-routes';
import versionsDocument from '../../../../../../fixtures/web-api/authoring-editor-versions/input.json';

const seeded = versionsDocument.body.items;
const oldestRow = seeded.at(-1);
const oldest = oldestRow === undefined ? '' : oldestRow.version_id;

function scenario() {
  return new AuthoringEditorFixtureScenario();
}

function openAs(
  subject: AuthoringEditorFixtureScenario,
  variants: Record<string, string>,
  method: string,
  path: string,
  body?: unknown,
  headers: Record<string, string> = {},
) {
  const route = path.split('?')[0] ?? path;
  return subject.entry(
    new HttpRequest(method, path, body, {
      headers: new HttpHeaders(headers),
      responseType: 'text',
    }),
    route,
    (operation) => variants[operation] ?? 'ok',
  );
}

function open(
  subject: AuthoringEditorFixtureScenario,
  method: string,
  path: string,
  body?: unknown,
  headers: Record<string, string> = {},
) {
  return openAs(subject, {}, method, path, body, headers);
}

describe('authoring editor fixtures', () => {
  it('validates every editor document against OpenAPI and routes its operation', () => {
    expect(FIXTURE_CATALOG_AUTHORING_EDITOR_ROWS.length).toBe(14);
    for (const { name, document } of FIXTURE_CATALOG_AUTHORING_EDITOR_ROWS) {
      expect(
        VALIDATOR.validateResponse(document.operation_id, document.status, document.body).errors,
        name,
      ).toEqual([]);
      expect(FIXTURE_ROUTES.some((route) => route.operationId === document.operation_id)).toBe(
        true,
      );
    }
  });

  it('saves the edited case, appends one version and reports the tag the next save quotes', () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_SAVED_CASE;
    const saved = open(
      subject,
      'PATCH',
      path,
      { title: 'Sign in with a password or a passkey' },
      {
        'If-Match': '"3"',
      },
    );
    expect(saved?.document.status).toBe(200);
    expect(saved?.document.body).toEqual({ version: 4, changed: true });
    expect(
      VALIDATOR.validateResponse('updateManualCase', 200, saved?.document.body).errors,
    ).toEqual([]);
    const read = open(subject, 'GET', path);
    const head = read?.document.body as ManualCaseDetailDto;
    expect(head.version).toBe(4);
    expect(read?.document.headers['ETag']).toBe('"4"');
    const timeline = open(
      subject,
      'GET',
      '/projects/p/authoring/versions?entity_id=' + EDITOR_SAVED_CASE,
    );
    const page = timeline?.document.body as AuthoringVersionListResponse;
    expect(seeded.length).toBe(3);
    expect(page.items.map((row) => row.version_no)).toEqual([4, 3, 2, 1]);
  });

  it('answers a no-op save with unchanged false and no new version', () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_SAVED_CASE;
    const saved = open(
      subject,
      'PATCH',
      path,
      { title: 'Sign in with a password' },
      {
        'If-Match': '"3"',
      },
    );
    expect(saved?.document.body).toEqual({ version: 3, changed: false });
    const timeline = open(
      subject,
      'GET',
      '/projects/p/authoring/versions?entity_id=' + EDITOR_SAVED_CASE,
    );
    const page = timeline?.document.body as AuthoringVersionListResponse;
    expect(page.items.length).toBe(seeded.length);
  });

  it('keeps the seeded version unchanged when empty step data is omitted on save', () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_SAVED_CASE;
    const head = structuredClone(open(subject, 'GET', path)?.document.body) as ManualCaseDetailDto;
    const timelinePath = '/projects/p/authoring/versions?entity_id=' + EDITOR_SAVED_CASE;
    const before = structuredClone(open(subject, 'GET', timelinePath)?.document.body);
    const saved = open(
      subject,
      'PATCH',
      path,
      {
        title: head.title,
        description: head.description,
        precondition: head.precondition,
        priority: head.priority,
        steps: head.steps.map(({ no, action, expected, data }) =>
          data === null ? { no, action, expected } : { no, action, expected, data },
        ),
      },
      { 'If-Match': '"3"' },
    );
    expect(saved?.document.status).toBe(200);
    expect(saved?.document.body).toEqual({ version: 3, changed: false });
    expect(open(subject, 'GET', timelinePath)?.document.body).toEqual(before);
    expect(open(subject, 'GET', path)?.document.body).toEqual(head);
    expect(head.steps[0]?.data).toBeNull();
  });

  it('versions populated step data and its removal but not an equivalent null save', () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_SAVED_CASE;
    const head = open(subject, 'GET', path)?.document.body as ManualCaseDetailDto;
    const populated = head.steps.map((step) => ({ ...step, data: 'new test data' }));
    expect(
      open(subject, 'PATCH', path, { steps: populated }, { 'If-Match': '"3"' })?.document.body,
    ).toEqual({ version: 4, changed: true });
    const omitted = head.steps.map(({ no, action, expected }) => ({ no, action, expected }));
    expect(
      open(subject, 'PATCH', path, { steps: omitted }, { 'If-Match': '"4"' })?.document.body,
    ).toEqual({ version: 5, changed: true });
    const cleared = omitted.map((step) => ({ ...step, data: null }));
    expect(
      open(subject, 'PATCH', path, { steps: cleared }, { 'If-Match': '"5"' })?.document.body,
    ).toEqual({ version: 5, changed: false });
    const page = open(
      subject,
      'GET',
      '/projects/p/authoring/versions?entity_id=' + EDITOR_SAVED_CASE,
    )?.document.body as AuthoringVersionListResponse;
    expect(page.items.map((row) => row.version_no)).toEqual([5, 4, 3, 2, 1]);
  });

  it("refuses a stale tag and leaves the other editor's head readable", () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_STALE_CASE;
    const refused = open(subject, 'PATCH', path, { title: 'Mine' }, { 'If-Match': '"2"' });
    expect(refused?.document.status).toBe(412);
    expect(
      VALIDATOR.validateResponse('updateManualCase', 412, refused?.document.body).errors,
    ).toEqual([]);
    const read = open(subject, 'GET', path);
    const head = read?.document.body as ManualCaseDetailDto;
    expect(head.version).toBe(3);
    expect(head.title).toBe('Sign in with a password and MFA');
    expect(read?.document.headers['ETag']).toBe('"3"');
  });

  it('refuses the invalid body with one pointer per refused field', () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_INVALID_CASE;
    const refused = open(subject, 'PATCH', path, { title: '' }, { 'If-Match': '"3"' });
    expect(refused?.document.status).toBe(422);
    expect(
      VALIDATOR.validateResponse('updateManualCase', 422, refused?.document.body).errors,
    ).toEqual([]);
    const envelope = refused?.document.body as ErrorEnvelope;
    expect(envelope.error.details?.map((detail) => detail.path)).toEqual([
      '/title',
      '/steps/0/action',
    ]);
  });

  it('answers a scoped refusal and a retryable outage before touching the stored case', () => {
    const subject = scenario();
    const path = '/projects/p/authoring/manual-cases/' + EDITOR_SAVED_CASE;
    const forbidden = openAs(subject, { getManualCase: 'forbidden' }, 'GET', path);
    expect(forbidden?.document.status).toBe(403);
    expect(forbidden?.document.body).toMatchObject({
      error: { code: 'forbidden', retryable: false },
    });
    const unavailable = openAs(
      subject,
      { updateManualCase: 'unavailable' },
      'PATCH',
      path,
      { title: 'Mine' },
      { 'If-Match': '"3"' },
    );
    expect(unavailable?.document.status).toBe(503);
    expect(unavailable?.document.headers['Retry-After']).toBe('1');
    expect(unavailable?.document.body).toMatchObject({
      error: { code: 'unavailable', retryable: true },
    });
    const read = open(subject, 'GET', path);
    const head = read?.document.body as ManualCaseDetailDto;
    expect(head.version).toBe(3);
    expect(head.title).toBe('Sign in with a password');
  });

  it('restores one stored version as a new row and keeps the earlier rows unchanged', () => {
    const subject = scenario();
    expect(oldest).not.toBe('');
    const restored = open(subject, 'POST', '/projects/p/authoring/versions/' + oldest + '/restore');
    expect(restored?.document.status).toBe(200);
    expect(restored?.document.body).toEqual({ version: 4 });
    expect(
      VALIDATOR.validateResponse('restoreAuthoringVersion', 200, restored?.document.body).errors,
    ).toEqual([]);
    const restoredBody = restored?.document.body as AuthoringRestoreResponse;
    expect(restoredBody.version).toBe(4);
    const headRead = open(
      subject,
      'GET',
      '/projects/p/authoring/manual-cases/' + EDITOR_SAVED_CASE,
    );
    const head = headRead?.document.body as ManualCaseDetailDto;
    expect(head.version).toBe(4);

    const first = open(
      subject,
      'GET',
      '/projects/p/authoring/versions?entity_id=' + EDITOR_SAVED_CASE + '&limit=2',
    );
    const firstPage = first?.document.body as AuthoringVersionListResponse;
    expect(firstPage.items.map((row) => row.change_kind)).toEqual(['restore', 'update']);
    const secondRow = firstPage.items.at(1);
    expect(firstPage.next_cursor).toBe(
      secondRow === undefined ? null : 'after:' + secondRow.version_id,
    );
    const second = open(
      subject,
      'GET',
      '/projects/p/authoring/versions?entity_id=' +
        EDITOR_SAVED_CASE +
        '&limit=2&cursor=' +
        encodeURIComponent(firstPage.next_cursor ?? ''),
    );
    const secondPage = second?.document.body as AuthoringVersionListResponse;
    expect(secondPage.items.map((row) => row.version_id)).toEqual(
      seeded.slice(1).map((row) => row.version_id),
    );
    expect(secondPage.next_cursor).toBeNull();
    const walked = [...firstPage.items, ...secondPage.items].map((row) => row.version_id);
    expect(new Set(walked).size).toBe(walked.length);
    expect(walked.length).toBe(seeded.length + 1);
  });

  it('gives the writer and reader review sessions exactly the save difference', () => {
    const write = authoringEditorPermissions('authoring-editor-write')?.document.body;
    const read = authoringEditorPermissions('authoring-editor-read')?.document.body;
    expect(VALIDATOR.validateResponse('listPermissions', 200, write).errors).toEqual([]);
    expect(VALIDATOR.validateResponse('listPermissions', 200, read).errors).toEqual([]);
    expect(JSON.stringify(read)).not.toContain('authoring:write');
    expect(JSON.stringify(write)).toContain('authoring:write');
    expect(authoringEditorPermissions('settings-admin')).toBeUndefined();
  });

  it('answers unknown editor ids with the corpus 404 envelopes', () => {
    const subject = scenario();
    for (const [method, path] of [
      ['GET', '/projects/p/authoring/manual-cases/a4430000-0000-4000-8000-000000009999'],
      ['GET', '/projects/p/authoring/versions/a4430000-0000-4000-8000-000000009999'],
      ['POST', '/projects/p/authoring/versions/a4430000-0000-4000-8000-000000009999/restore'],
    ] as const) {
      const entry = open(subject, method, path);
      expect(entry?.document.status, method + ' ' + path).toBe(404);
      expect(entry?.document.body).toMatchObject({ error: { code: 'not_found' } });
    }
  });
});
