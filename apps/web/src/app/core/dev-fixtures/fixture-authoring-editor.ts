// The manual-case editor's review states. The corpus rows hold the response documents (the case,
// the timeline page, one stored version, and every refusal); this scenario adds the transitions a
// static document cannot show: a save that bumps the case and appends a version, a stale tag that
// leaves the server head readable for the conflict panel, and a restore that appends one more
// version while the earlier rows stay as they were.
//
// The state is selected by the case id the reviewer opens, so one browser walk covers the happy
// path, the conflict and the invalid body:
//   a4430000-0000-4000-8000-000000000001  saved normally; its timeline is seeded
//   a4430000-0000-4000-8000-000000000002  another editor already saved; every PATCH answers 412
//   a4430000-0000-4000-8000-000000000003  every PATCH answers 422 with two field pointers
// A `?fixture.variant.<operationId>=<variant>` toggle still wins, so the corpus refusals stay
// reachable on any case.
//
// Dev-only, like the rest of `core/dev-fixtures`: see the module comment of `fixture-toggles.ts`.

import type { HttpRequest } from '@angular/common/http';
import type {
  AuthoringVersionDetailDto,
  AuthoringVersionListItemDto,
  AuthoringVersionListResponse,
  ManualCaseDetailDto,
  ManualCaseStepDto,
  UpdateManualCaseRequest,
} from '@evidgate/sdk';
import { newUuid } from '../http/random-id';
import type { FixtureEntry } from './fixture-catalog-entries';
import { findFixtureEntry } from './fixture-catalog';
import { queryParameter } from './fixture-routes';
import permissions from '../../../../../../contracts/permissions.json';
import caseDocument from '../../../../../../fixtures/web-api/authoring-editor-case/input.json';
import currentDocument from '../../../../../../fixtures/web-api/authoring-editor-case-current/input.json';
import validationTargetDocument from '../../../../../../fixtures/web-api/authoring-editor-case-validation-target/input.json';
import versionsDocument from '../../../../../../fixtures/web-api/authoring-editor-versions/input.json';
import versionDocument from '../../../../../../fixtures/web-api/authoring-editor-version/input.json';

/** The case whose saves succeed and whose timeline the tab renders. */
export const EDITOR_SAVED_CASE = 'a4430000-0000-4000-8000-000000000001';
/** The case whose every save is refused as stale, with a differing server head to diff. */
export const EDITOR_STALE_CASE = 'a4430000-0000-4000-8000-000000000002';
/** The case whose every save is refused as invalid. */
export const EDITOR_INVALID_CASE = 'a4430000-0000-4000-8000-000000000003';

/** How many stored versions one timeline page holds when the request names no limit. */
const DEFAULT_PAGE = 50;

/**
 * The editor's two review sessions, differing by exactly the save grant. Both are copied verbatim
 * from the permission catalogue; a variant that names neither session is not this supplement's.
 */
export function authoringEditorPermissions(variant: string): FixtureEntry | undefined {
  if (variant !== 'authoring-editor-write' && variant !== 'authoring-editor-read') return undefined;
  const codes = [
    'authoring:read',
    'organization:read',
    'project:read',
    ...(variant === 'authoring-editor-write' ? ['authoring:write'] : []),
  ];
  return {
    name: variant,
    document: {
      operation_id: 'listPermissions',
      variant,
      status: 200,
      headers: {},
      latency_ms: 0,
      body: {
        items: permissions.permissions.filter((entry) => codes.includes(entry.code)),
        next_cursor: null,
      },
    },
  };
}

interface CaseState {
  head: ManualCaseDetailDto;
  /** Newest first, the order route 22 declares. */
  versions: AuthoringVersionDetailDto[];
}

/** A stable digest shape for a version the scenario appends; no content is actually hashed. */
function digest(versionNo: number): string {
  return 'sha256:' + versionNo.toString(16).padStart(64, '0');
}

function snapshot(view: ManualCaseDetailDto): Record<string, unknown> {
  return {
    case_id: view.case_id,
    code: view.code,
    checklist_item_id: view.checklist_item_id,
    node_id: view.node_id,
    title: view.title,
    description: view.description,
    precondition: view.precondition,
    steps: view.steps,
    priority: view.priority,
    state: view.state,
  };
}

/** The row projection route 22 declares: the same members, without the stored `content`. */
function listItem(version: AuthoringVersionDetailDto): AuthoringVersionListItemDto {
  return {
    version_id: version.version_id,
    entity_kind: version.entity_kind,
    entity_id: version.entity_id,
    version_no: version.version_no,
    batch_id: version.batch_id,
    change_kind: version.change_kind,
    source: version.source,
    content_hash: version.content_hash,
    author: version.author,
    reason: version.reason,
    ai_run_id: version.ai_run_id,
    created_at: version.created_at,
  };
}

/** The stored snapshot's own members, applied back onto the case a restore names. */
function restored(
  head: ManualCaseDetailDto,
  content: Record<string, unknown>,
): ManualCaseDetailDto {
  const steps = Array.isArray(content['steps']) ? (content['steps'] as ManualCaseStepDto[]) : null;
  return {
    ...head,
    title: typeof content['title'] === 'string' ? content['title'] : head.title,
    description:
      content['description'] === null || typeof content['description'] === 'string'
        ? content['description']
        : head.description,
    precondition:
      content['precondition'] === null || typeof content['precondition'] === 'string'
        ? content['precondition']
        : head.precondition,
    steps: steps && steps.length > 0 ? steps : head.steps,
    priority:
      content['priority'] === 'critical' ||
      content['priority'] === 'high' ||
      content['priority'] === 'medium' ||
      content['priority'] === 'low'
        ? content['priority']
        : head.priority,
  };
}

/** The edited members of a PATCH; an absent member is unchanged, `null` clears a text member. */
function edited(head: ManualCaseDetailDto, body: unknown): ManualCaseDetailDto {
  const patch = (typeof body === 'object' && body !== null ? body : {}) as UpdateManualCaseRequest;
  return {
    ...head,
    ...(typeof patch.title === 'string' ? { title: patch.title } : {}),
    ...('description' in patch ? { description: patch.description ?? null } : {}),
    ...('precondition' in patch ? { precondition: patch.precondition ?? null } : {}),
    ...(patch.steps ? { steps: patch.steps } : {}),
    ...(patch.priority ? { priority: patch.priority } : {}),
  };
}

function changed(before: ManualCaseDetailDto, after: ManualCaseDetailDto): boolean {
  return (
    before.title !== after.title ||
    before.description !== after.description ||
    before.precondition !== after.precondition ||
    JSON.stringify(before.steps) !== JSON.stringify(after.steps) ||
    before.priority !== after.priority
  );
}

export class AuthoringEditorFixtureScenario {
  private readonly projects = new Map<string, Map<string, CaseState>>();
  private appended = 0;

  entry(
    request: HttpRequest<unknown>,
    path: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    const single = /^\/projects\/([^/]+)\/authoring\/manual-cases\/([^/]+)$/.exec(path);
    if (single?.[1] && single[2]) return this.caseEntry(request, single[1], single[2], variantFor);
    const restore = /^\/projects\/([^/]+)\/authoring\/versions\/([^/]+)\/restore$/.exec(path);
    if (restore?.[1] && restore[2])
      return this.restoreEntry(request, restore[1], restore[2], variantFor);
    const list = /^\/projects\/([^/]+)\/authoring\/versions$/.exec(path);
    if (list?.[1]) return this.versionList(request, list[1], variantFor);
    const one = /^\/projects\/([^/]+)\/authoring\/versions\/([^/]+)$/.exec(path);
    if (one?.[1] && one[2]) return this.versionEntry(request, one[1], one[2], variantFor);
    return undefined;
  }

  private caseEntry(
    request: HttpRequest<unknown>,
    project: string,
    caseId: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    if (request.method === 'PATCH') return this.patch(request, project, caseId, variantFor);
    if (request.method !== 'GET') return undefined;
    return this.read(project, caseId, variantFor);
  }

  private read(
    project: string,
    caseId: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    const variant = variantFor('getManualCase');
    if (variant !== 'ok') return findFixtureEntry('getManualCase', variant);
    const state = this.state(project, caseId);
    return state
      ? this.answer('getManualCase', state.head, 200, '"' + state.head.version + '"')
      : findFixtureEntry('getManualCase', 'not-found');
  }

  private patch(
    request: HttpRequest<unknown>,
    project: string,
    caseId: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    const toggle = variantFor('updateManualCase');
    if (toggle !== 'ok') return findFixtureEntry('updateManualCase', toggle);
    const state = this.state(project, caseId);
    if (state === undefined) return findFixtureEntry('updateManualCase', 'not-found');
    if (caseId === EDITOR_STALE_CASE) {
      // The stale answer leaves the head as the other editor left it, so the panel can diff.
      state.head = currentDocument.body as ManualCaseDetailDto;
      return findFixtureEntry('updateManualCase', 'conflict');
    }
    if (caseId === EDITOR_INVALID_CASE) return findFixtureEntry('updateManualCase', 'validation');
    if (request.headers.get('If-Match') !== '"' + state.head.version + '"')
      return findFixtureEntry('updateManualCase', 'conflict');
    const next = edited(state.head, this.bodyOf(request));
    if (!changed(state.head, next))
      return this.answer('updateManualCase', { version: state.head.version, changed: false }, 200);
    const committed: ManualCaseDetailDto = {
      ...next,
      version: state.head.version + 1,
      updated_at: this.instant(),
    };
    state.head = committed;
    state.versions.unshift(this.versionRow(committed, 'update'));
    return this.answer(
      'updateManualCase',
      { version: committed.version, changed: true },
      200,
      '"' + committed.version + '"',
    );
  }

  private versionList(
    request: HttpRequest<unknown>,
    project: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    const variant = variantFor('listAuthoringVersions');
    if (variant !== 'ok') return findFixtureEntry('listAuthoringVersions', variant);
    const caseId = queryParameter(request.urlWithParams, 'entity_id');
    const selected = caseId === null ? undefined : this.cases(project).get(caseId);
    const rows = (selected?.versions ?? []).map(listItem);
    const limit = Number(queryParameter(request.urlWithParams, 'limit') ?? '') || DEFAULT_PAGE;
    const after = queryParameter(request.urlWithParams, 'cursor');
    const start =
      after !== null && after.startsWith('after:')
        ? rows.findIndex((row) => row.version_id === after.slice('after:'.length)) + 1
        : 0;
    const page = rows.slice(start, start + limit);
    const last = page.at(-1);
    return this.answer('listAuthoringVersions', {
      items: page,
      next_cursor: start + page.length < rows.length && last ? 'after:' + last.version_id : null,
    });
  }

  private versionEntry(
    request: HttpRequest<unknown>,
    project: string,
    versionId: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    if (request.method !== 'GET') return undefined;
    const variant = variantFor('getAuthoringVersion');
    if (variant !== 'ok') return findFixtureEntry('getAuthoringVersion', variant);
    const version = this.findVersion(project, versionId);
    return version
      ? this.answer('getAuthoringVersion', version, 200, '"' + version.content_hash + '"')
      : findFixtureEntry('getAuthoringVersion', 'not-found');
  }

  private restoreEntry(
    request: HttpRequest<unknown>,
    project: string,
    versionId: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    if (request.method !== 'POST') return undefined;
    const variant = variantFor('restoreAuthoringVersion');
    if (variant !== 'ok') return findFixtureEntry('restoreAuthoringVersion', variant);
    for (const state of this.cases(project).values()) {
      const version = state.versions.find((row) => row.version_id === versionId);
      if (version === undefined) continue;
      const restoredHead: ManualCaseDetailDto = {
        ...restored(state.head, version.content),
        version: state.head.version + 1,
        updated_at: this.instant(),
      };
      state.head = restoredHead;
      state.versions.unshift(this.versionRow(restoredHead, 'restore', version.content));
      return this.answer('restoreAuthoringVersion', { version: restoredHead.version }, 200);
    }
    return findFixtureEntry('restoreAuthoringVersion', 'not-found');
  }

  /** One version row for a case the scenario just wrote; ids are fresh, the digest is a shape. */
  private versionRow(
    view: ManualCaseDetailDto,
    changeKind: 'update' | 'restore',
    content = snapshot(view),
  ): AuthoringVersionDetailDto {
    this.appended += 1;
    return {
      version_id: newUuid(),
      entity_kind: 'manual_test_case',
      entity_id: view.case_id,
      version_no: view.version,
      batch_id: newUuid(),
      change_kind: changeKind,
      source: 'manual',
      content_hash: digest(view.version),
      author: null,
      reason: null,
      ai_run_id: null,
      created_at: this.instant(),
      content,
    };
  }

  private findVersion(project: string, versionId: string): AuthoringVersionDetailDto | undefined {
    for (const state of this.cases(project).values()) {
      const version = state.versions.find((row) => row.version_id === versionId);
      if (version !== undefined) return version;
    }
    return undefined;
  }

  private state(project: string, caseId: string): CaseState | undefined {
    return this.cases(project).get(caseId);
  }

  /** The three documented cases, seeded once per project from the corpus documents. */
  private cases(project: string): Map<string, CaseState> {
    const known = this.projects.get(project);
    if (known !== undefined) return known;
    const seeded = new Map<string, CaseState>();
    const stored = (versionsDocument.body as AuthoringVersionListResponse).items;
    const timeline = stored.map((item) =>
      this.seedVersion(item, versionDocument.body as AuthoringVersionDetailDto),
    );
    seeded.set(EDITOR_SAVED_CASE, {
      head: caseDocument.body as ManualCaseDetailDto,
      versions: timeline,
    });
    seeded.set(EDITOR_STALE_CASE, {
      head: currentDocument.body as ManualCaseDetailDto,
      versions: [],
    });
    seeded.set(EDITOR_INVALID_CASE, {
      head: validationTargetDocument.body as ManualCaseDetailDto,
      versions: [],
    });
    this.projects.set(project, seeded);
    return seeded;
  }

  /** A seeded timeline row: the corpus row's fields with the stored version's own content. */
  private seedVersion(
    item: AuthoringVersionListItemDto,
    stored: AuthoringVersionDetailDto,
  ): AuthoringVersionDetailDto {
    return { ...item, content: stored.content };
  }

  private bodyOf(request: HttpRequest<unknown>): unknown {
    if (typeof request.body !== 'string') return request.body;
    try {
      return JSON.parse(request.body) as unknown;
    } catch {
      return null;
    }
  }

  /** Descending instants for the rows the scenario appends, so newest-first order stays stable. */
  private instant(): string {
    return new Date(Date.UTC(2026, 10, 5) + this.appended * 1000).toISOString();
  }

  private answer(operation: string, body: unknown, status = 200, etag?: string): FixtureEntry {
    return {
      name: 'authoring-editor-review-state',
      document: {
        operation_id: operation,
        variant: 'ok',
        status,
        headers: etag === undefined ? {} : { ETag: etag },
        latency_ms: 0,
        body,
      },
    };
  }
}
