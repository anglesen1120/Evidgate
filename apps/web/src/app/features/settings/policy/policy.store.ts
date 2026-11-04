// 09.7.1-T4 (plan 09.7.1 §2 AC1/AC4/AC5, §5, §8 S3, §10 T4; COORDINATOR DECISIONS S83 P1–P4):
// the Settings > Policy tab's state, as a plain Angular signals service — private `signal`s,
// public `asReadonly()`/`computed`, no writable state escaping the class — shaped exactly like
// `secrets/secrets.store.ts`, because the workspace does not depend on `@ngrx/signals`.
//
// One instance per page (`providers: [PolicyStore]` on the page), because the store owns the
// cursor of one revision's list and the bodies it has read.
//
// Seven rules shape every method:
//
// 1. A revision id is the page's only key (S83 P2). `open()` starts from a revision id, learns the
//    PROJECT from the revision itself (`revision.project_id`, never from the URL or the route
//    scope), and reads the revision list through that same id. The project's activation and head
//    revision have their own reads (`policy-reads.ts`), which start once the project is known and
//    never decide whether the list is shown. An id that is not UUID-shaped is the "no revision
//    selected" state and requests nothing at all.
// 2. No existence leak. A revision of a project this session is not in is refused as `not_found` —
//    the same state a `404`/`403` renders — and nothing else is requested, so the tab cannot be used
//    to ask whether another project's revision exists.
// 3. The list is read in pages (00 §9.1, W7). `loadMore()` follows `next_cursor` one page per call,
//    bounded by {@link MAX_CURSOR_PAGES} and refusing a cursor it already followed — both contract
//    violations throw a plain `Error`, never a partial list passed off as complete.
// 4. The head is the server's, and so is the active revision. `headRevisionId()` is the LAST list
//    answer's `ETag` (unquoted) — the value a publish quotes in `If-Match` — and `activeRevision()`
//    is the listed row whose `state` is `active`. Neither is ever set optimistically: a publish
//    changes them only once a list answer says so.
// 5. Publication checks before it sends (S83 P4). The store refuses locally, with no request, a
//    caller without `policy:write` (the control is absent from the DOM, so this is defence in
//    depth), a body that is not a JSON object, and a body over 65 536 UTF-8 bytes — measured on the
//    COMPACT wire body, never on the pretty-printed text, so indentation cannot cost the user a
//    revision the server would have accepted (`policy-body-size.ts`); the server's closed schema is
//    the authority on everything else, so a `422` is surfaced VERBATIM. The textarea's text is a
//    parameter, never state: no signal, field, URL or log line of this store holds a policy body
//    (`no-secret-field-in-store` scans store fields for exactly this class of mistake).
// 6. A `412` is the end of an attempt. The caller gets `conflict` with the current head the server
//    named, and nothing is retried or resubmitted — the deliberate reload is the user's.
// 7. The caller gets the publish, the screen gets the refresh. A `created`/`unchanged` answer is
//    returned as soon as the server confirmed it; re-reading the list is a background read it
//    neither awaits nor can fail, because a slow or refused list must never cost the user the
//    revision the server has already stored.
// 8. Every body read passes the two checks rule 1 states (D1 delta finding; R9). The `412`
//    envelope's head id is server text and reaches a read without going through `open`, so
//    `loadBody` applies the same UUID shape check (no request at all when it fails) and refuses a
//    body whose `project_id` is not the project the READ WAS ASKED FOR, as `not_found` and without
//    caching it. That project is captured when the call is made, never re-read when the answer
//    lands (R9): a page that switched project mid-read still gets the legitimate body of the
//    project that asked, and a call made with nothing on screen compares against nothing.

import { computed, inject, Injectable, signal } from '@angular/core';
import type {
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { PolicyDataService, type PolicyRevisionPage } from '../../../core/data/policy-data.service';
import {
  ApiRequestError,
  isApiRequestError,
  type ApiError,
  type ApiErrorKind,
} from '../../../core/http/api-error';
import { settledRead } from '../../../core/http/settled-read';
import { SessionStore } from '../../../core/session/session.store';
import { conflictFromError } from '../../../shared/ui/conflict/conflict-details';
import { POLICY_BODY_MAX_BYTES, policyBodyBytes } from './policy-body-size';
import { PolicyProjectReads } from './policy-reads';

/** The tab's read state: `idle` before a revision is opened, `not_found` for every refusal. */
export type PolicyPhase = 'idle' | 'loading' | 'ready' | 'error' | 'not_found';

/**
 * What one publish attempt hands its caller. `created`/`unchanged` carry the stored revision, the
 * server's own answer to a `201`/`200`; `conflict` is a `412` with the head the server named;
 * `refused` is every other refusal (a `422`, a `413`, a rate limit, a transport failure) verbatim,
 * never paraphrased; and `too_large`/`invalid_json` are the two local refusals, where nothing at
 * all was sent.
 */
export type PublishResult =
  | { readonly kind: 'created' | 'unchanged'; readonly revision: PolicyRevisionDto }
  | {
      readonly kind: 'conflict';
      readonly error: ApiError;
      readonly currentRevisionId: string | null;
    }
  | { readonly kind: 'refused'; readonly error: ApiError }
  | { readonly kind: 'too_large'; readonly bytes: number }
  | { readonly kind: 'invalid_json' }
  | { readonly kind: 'busy' };

/** The frozen empty rows, so an empty store and a cleared scope share one array identity. */
const NO_REVISIONS: readonly PolicyRevisionSummaryDto[] = [];

/**
 * How many revision bodies the diff and the publish seed may hold at once. The oldest read is
 * dropped, because a body is only needed while it is on screen and a project's history is unbounded.
 */
const MAX_CACHED_BODIES = 8;

/**
 * The shape of a revision id: five hex groups, `8-4-4-4-12`, any version. Shape only — the server
 * decides existence and access — which is what makes a stray URL parameter cost no request and
 * reveal nothing.
 */
const REVISION_ID_SHAPE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * The refusals that are indistinguishable from "there is no such revision": a `404` (00 §9.2 also
 * answers it for another tenant's or another project's id) and a `403` for a reason other than the
 * antiforgery token. Both become the same `not_found` phase, so the tab cannot tell an id it may not
 * see from an id that does not exist.
 *
 * `csrf_failed` is deliberately NOT one of them (review finding F2): it says nothing about whether
 * the revision exists — it says the antiforgery token was refused — so folding it into `not_found`
 * would hide a fault the user can act on (reload and retry) behind "there is nothing here". It is
 * `error` with the server's own `ApiError`, like every other refusal that is not about existence.
 */
const REFUSAL_KINDS: readonly ApiErrorKind[] = ['not_found', 'forbidden'];

/** No fields to compare in a policy conflict: the dialog learns the head, not a field snapshot. */
const NO_CONFLICT_FIELDS: Readonly<Record<string, string>> = {};

/**
 * The typed failure a publish reports when the rejection is not the server's answer — an adapter
 * cancellation or a bug. It is the store's own copy (never text taken from a thrown value, which may
 * carry request data it must not surface) and it claims nothing that was not observed: a request
 * whose outcome is unknown is not retried automatically, because the head may have moved.
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'policy_publish_unresolved',
  message:
    'The request did not complete, so nothing was confirmed; reload the revisions before publishing again.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The typed failure a publish reports when there is no project to publish to. The page cannot
 * reach this (the publish control only appears once a list answer named a head), so it exists to
 * keep the "nothing was sent" promise honest rather than to be rendered.
 */
const NO_SCOPE_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'policy_scope_unavailable',
  message: 'No policy revision is open, so nothing was sent.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The typed failure a publish reports when this session does not hold `policy:write` (review finding
 * F5). The publish control is absent from the DOM without the code (S83 P1), so the page cannot
 * reach this; it is defence in depth, the same way a route repeats an authorization rule its UI
 * already hides. Nothing is sent, and the server's own refusal is never pre-empted — the request
 * that could have been legitimate is simply not made.
 */
const NO_WRITE_FAILURE: ApiError = {
  kind: 'forbidden',
  status: 0,
  code: 'policy_write_forbidden',
  message: 'This session may not publish policy revisions, so nothing was sent.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The typed refusal a body read answers with NO request at all (delta finding D1): the id is not
 * revision-shaped, so there is nothing to ask the server about. The `412` envelope's head id is
 * server text, and it is the one id in this tab that reaches a read without having passed the
 * UUID check {@link PolicyStore.open} applies to the page's own key; refusing it here keeps that
 * one entry point to the same rule. `not_found` is the kind because that is the state both callers
 * already render for "there is no such revision", so a hostile answer cannot open a state the tab
 * has no copy for.
 */
const MALFORMED_REVISION_FAILURE: ApiError = {
  kind: 'not_found',
  status: 0,
  code: 'policy_revision_id_invalid',
  message: 'That is not a policy revision id, so no revision was read.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The typed refusal a body read answers with when the revision it DID read belongs to another
 * project than the one THE READ WAS ASKED FOR (delta finding D1; the captured project is R9).
 * Exactly the same state a `404` renders ({@link REFUSAL_KINDS}), so a body cannot be used to learn
 * that an id of another project exists, and such an answer is never cached: it belongs to a view
 * this store does not own.
 */
const FOREIGN_REVISION_FAILURE: ApiError = {
  kind: 'not_found',
  status: 0,
  code: 'policy_revision_not_found',
  message: 'The policy revision was not found in this project.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The body a publish sends: the textarea's text parsed, or `null` when it is not a JSON OBJECT.
 * An array and a scalar are refused with the same `invalid_json`, because the wire body is a closed
 * object schema and sending either would only spend a request to learn that.
 */
function parsePolicyObject(bodyText: string): Record<string, unknown> | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(bodyText);
  } catch {
    return null;
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return null;
  }
  return parsed as Record<string, unknown>;
}

@Injectable()
export class PolicyStore {
  private readonly data = inject(PolicyDataService);
  private readonly session = inject(SessionStore);
  /**
   * The project's activation and head revision, read beside the list. Exposed as is: the summary
   * renders their phases, which are independent of the list's own phase.
   */
  readonly reads = inject(PolicyProjectReads);

  private readonly revisionsSignal = signal<readonly PolicyRevisionSummaryDto[]>(NO_REVISIONS);
  private readonly phaseSignal = signal<PolicyPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectIdSignal = signal<string | null>(null);
  private readonly headRevisionIdSignal = signal<string | null>(null);
  private readonly activationSignal = signal<PolicyActivationStateDto | null>(null);
  /**
   * The double-submit lock. It is this flag, not the absence of an answer, that decides whether a
   * publish may start: the request is out and a second press would append a second revision (or
   * answer `busy` into a dialog that already closed).
   */
  private readonly publishingSignal = signal(false);

  /**
   * The revision id the list route is asked about. The route's path parameter is a revision id —
   * any of the project's — so every page of one list names the same anchor, and a publish (which
   * appends a NEW id) never invalidates it.
   */
  private anchorRevisionId: string | null = null;
  /**
   * The last revision id this store was ASKED to open, shape-checked, kept even when that open
   * failed before it could read the list (review finding F6). It is what {@link reload} retries from
   * when there is no anchor yet — a `503` on the revision read leaves nothing to re-read the list
   * through, and a reload that silently did nothing would strand the page's own retry. `null` until
   * an id is opened, so a reload before any open stays the no-op it was.
   */
  private lastRequestedRevisionId: string | null = null;
  /** The cursors this store already asked for, so a repeated one is an error, not a loop. */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current anchor, the first page included; bounded by the page cap. */
  private pagesRead = 1;
  /** Bumped by every read; a read of an older generation never writes state. */
  private generation = 0;
  /**
   * The bodies read for the diff and the publish seed, oldest first so the entry to drop is the
   * first key. A body is immutable once stored, so a cached one is never stale.
   */
  private readonly bodyCache = new Map<string, PolicyRevisionDto>();
  /**
   * The body reads still in flight, keyed by revision id (review finding F3). The diff and the publish
   * dialog can ask for the same revision in the same tick — the dialog seeds itself from the row the
   * diff is already measuring — and a second `GET` for an immutable revision would be pure waste. A
   * later caller joins the read already out instead of starting one of its own; the entry is dropped
   * the moment that read settles, so a failure is retried on the next call rather than replayed.
   */
  private readonly bodyReads = new Map<string, Promise<PolicyRevisionDto>>();

  /** The project's revisions, ascending as served, as many cursor pages as were followed. */
  readonly revisions = this.revisionsSignal.asReadonly();
  /** The read state the page renders. */
  readonly phase = this.phaseSignal.asReadonly();
  /** The server's typed failure for the revision or list read; `null` when it never failed. */
  readonly loadError = this.loadErrorSignal.asReadonly();
  /** The project the loaded list belongs to, from the opened revision; `null` before that. */
  readonly projectId = this.projectIdSignal.asReadonly();
  /** True while the server named another page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);
  /**
   * The head revision id the last list answer named (its `ETag`, unquoted) — the value a publish
   * quotes in `If-Match`, and `null` until a list answer says what the head is.
   */
  readonly headRevisionId = this.headRevisionIdSignal.asReadonly();
  /** The listed revision whose `state` is `active`; `null` when the list names none. */
  readonly activeRevision = computed(
    () => this.revisionsSignal().find((revision) => revision.state === 'active') ?? null,
  );
  /**
   * The project's policy activation, as the seam {@link applyActivation} was handed: the served
   * object once {@link PolicyProjectReads} read it, `null` while that read is out or when the
   * project has no activation (or the caller may not read it).
   */
  readonly activation = this.activationSignal.asReadonly();
  /** `policy_activation.mode`, VERBATIM — never derived from a revision's `state`. */
  readonly mode = computed(() => this.activationSignal()?.mode ?? null);
  /** True while a publish request is out; a second {@link publish} answers `busy`. */
  readonly publishing = this.publishingSignal.asReadonly();

  /**
   * Opens the named revision: reads it, refuses it unless its project is one this session is in,
   * then reads that project's first revision page.
   *
   * An id that is not UUID-shaped clears the tab to `idle` (the "no revision selected" state) and
   * requests nothing. A refusal — `404`, `403`, or a revision of a project this session does not
   * have — leaves the tab in `not_found` with no second request and no way to tell the three apart.
   *
   * Never rejects: the page calls `void store.open(...)` from a click handler and an effect, so a
   * rejection would be an unhandled one. A cancellation or a bug leaves the typed error `null`,
   * which the page renders as its generic error state rather than as a message the server never sent.
   */
  async open(revisionId: string): Promise<void> {
    const id = revisionId.trim();
    if (!REVISION_ID_SHAPE.test(id)) {
      this.reset();
      return;
    }

    // The reload retries from here when this open never reached the list (F6).
    this.lastRequestedRevisionId = id;
    const generation = ++this.generation;
    this.clearState();
    this.phaseSignal.set('loading');

    const revision = await settledRead(() => this.data.getRevision(id));
    // A newer open (or a reset) owns the state now: this answer describes a revision nobody renders.
    if (generation !== this.generation) {
      return;
    }
    if (!revision.ok) {
      this.settleFailure(revision.error);
      return;
    }

    const projectId = revision.value.project_id;
    if (!this.session.projects().some((project) => project.id === projectId)) {
      // The revision exists and is readable, but it belongs to a project this session is not in.
      // The tab answers exactly what a 404 answers, so it cannot be used to probe for revisions.
      this.phaseSignal.set('not_found');
      return;
    }

    this.projectIdSignal.set(projectId);
    this.startReads(projectId);
    await this.readList(id, generation, true);
  }

  /**
   * Follows `next_cursor` once and appends the page, so the screen keeps the rows it already shows
   * while the next one is on its way (00 §9.1 keyset cursor; never "load all").
   *
   * A cursor already followed, or a {@link MAX_CURSOR_PAGES}-th page that still names another one,
   * is a contract violation and throws a plain `Error` — the same two refusals as
   * `IdentityDataService`, so a broken cursor costs visibility of the bug, not a hung tab. A typed
   * server refusal is not a bug: it becomes `loadError` and the rows already read stay on screen.
   */
  async loadMore(): Promise<void> {
    const anchor = this.anchorRevisionId;
    const projectId = this.projectIdSignal();
    const cursor = this.nextCursorSignal();
    if (anchor === null || projectId === null || cursor === null) {
      return;
    }
    if (this.followedCursors.has(cursor)) {
      throw new Error(`the policy revision list repeated cursor "${cursor}"`);
    }
    if (this.pagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the policy list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.generation;
    const read = await settledRead(() => this.data.listRevisions(anchor, cursor));
    // A newer read (another open, a reload, a publish's refresh) owns the state now.
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      this.loadErrorSignal.set(read.error.error);
      if (this.revisionsSignal().length === 0) {
        this.phaseSignal.set('error');
      }
      return;
    }

    this.followedCursors.add(cursor);
    this.pagesRead += 1;
    const page = read.value;
    this.revisionsSignal.update((rows) => [...rows, ...page.items]);
    this.nextCursorSignal.set(page.nextCursor);
    // Every page of one list names the same head; taking it from the newest answer keeps the
    // `If-Match` source honest if a publish committed elsewhere in between.
    this.headRevisionIdSignal.set(page.headRevisionId);
    // A page that arrived IS the answer the earlier failure withheld (F7): the alert a refused page
    // raised must not outlive the read that succeeded.
    this.loadErrorSignal.set(null);
  }

  /**
   * Re-reads the list from its first page — the explicit reload a `412` conflict dialog offers, and
   * the page's own retry. It also restarts the project reads, so one control retries every read the
   * summary renders. It settles `error`/`not_found` on a refusal (so the dialog's reload path
   * sees the failure) and never rejects.
   *
   * With no anchor there is no list to re-read: the open that was meant to set one failed before the
   * list (a `503` on the revision read, say), so this retries that open from the id it was asked for
   * (F6). A store that was never opened has nothing to retry and stays the no-op it was.
   */
  async reload(): Promise<void> {
    const anchor = this.anchorRevisionId;
    if (anchor === null) {
      if (this.lastRequestedRevisionId !== null) {
        await this.open(this.lastRequestedRevisionId);
      }
      return;
    }
    const projectId = this.projectIdSignal();
    if (projectId !== null) {
      this.startReads(projectId);
    }
    const generation = ++this.generation;
    await this.readList(anchor, generation, true);
  }

  /**
   * One revision with its body, cached for the diff and the publish seed: at most
   * {@link MAX_CACHED_BODIES} bodies, the oldest dropped. A cached body is returned without a
   * request (a stored revision is immutable) and re-inserted so it counts as the newest.
   *
   * Two checks run BEFORE a body is handed to a caller (delta finding D1, refined by R9). The id
   * must be revision-shaped ({@link REVISION_ID_SHAPE}), exactly as {@link open} requires of the
   * page's own key: a body read is the one path a `412` envelope's head id reaches, so a malformed
   * answer is refused with no request at all. And a body the server answers for a revision of
   * ANOTHER project than the one the READ WAS ASKED FOR is refused like a missing one and never
   * cached — the same existence promise {@link open} keeps.
   *
   * The project is captured when the CALL is made, before anything is read (R9), and the answer is
   * compared against that captured value — never against the signal at arrival. The page may switch
   * project while the answer is on its way, and the body of the project that asked is still that
   * caller's; comparing at arrival refused exactly that legitimate body, and a reset or a switch in
   * the same window skipped the check altogether. A call made while NOTHING is open captures
   * nothing and so compares against nothing: there is no project to compare with, and the tab asks
   * for a body only after a list already named the project. A cached body passes the same check on
   * every call, against the project that call is for, so the cache is never a way around it.
   *
   * Concurrent calls for the same id share ONE read ({@link bodyReads}): the dialog and the diff
   * often ask in the same tick, and the second request would only repeat the first. Each caller
   * still has its own captured project checked, because the joined call was asked for it too. A
   * read that settles after a {@link reset} or after the page moved to another revision still
   * answers its own caller — it was asked for exactly that revision — but its body is NOT cached:
   * the cache belongs to the view that is gone.
   *
   * REJECTS with the server's `ApiRequestError` when the read is refused, and with this store's own
   * typed refusals for the two cases above: both callers (the diff and the publish dialog) have a
   * failure state of their own, and swallowing the refusal here would leave them seeding an editor
   * with nothing.
   */
  async loadBody(revisionId: string): Promise<PolicyRevisionDto> {
    const id = revisionId.trim();
    if (!REVISION_ID_SHAPE.test(id)) {
      // No request: the id is the server's own text in the `412` case, and it is not a revision id.
      throw new ApiRequestError(MALFORMED_REVISION_FAILURE);
    }
    // R9: the project THIS CALL is for, captured before anything is read — never the project on
    // screen when an answer happens to land. A page that switches project mid-read still gets the
    // legitimate body of the project that asked, and a call made with nothing open has nothing to
    // compare against (`null`).
    const expectedProjectId = this.projectIdSignal();
    const cached = this.bodyCache.get(id);
    if (cached !== undefined) {
      const body = this.bodyOfRequestedProject(cached, expectedProjectId);
      this.bodyCache.delete(id);
      this.bodyCache.set(id, cached);
      return body;
    }
    const shared = this.bodyReads.get(id);
    if (shared !== undefined) {
      // The joined caller was asked for this revision too, so its own captured project decides.
      return shared.then((revision) => this.bodyOfRequestedProject(revision, expectedProjectId));
    }
    const generation = this.generation;
    const read = this.data.getRevision(id).then((revision) => {
      const body = this.bodyOfRequestedProject(revision, expectedProjectId);
      if (generation === this.generation) {
        this.cacheBody(revision);
      }
      return body;
    });
    this.bodyReads.set(id, read);
    try {
      return await read;
    } finally {
      // Only this read's own entry: a later call may already have started the next one.
      if (this.bodyReads.get(id) === read) {
        this.bodyReads.delete(id);
      }
    }
  }

  /**
   * The check rule 8 states, applied to ONE body (delta finding D1, refined by R9).
   *
   * `expectedProjectId` is the project the CALL was made for, captured by {@link loadBody} before
   * anything is read: a body of any other project is refused as `not_found` — the state a `404`
   * renders — so a hostile `412` head id, or a buggy answer, can neither reach the diff nor be
   * probed for. `null` means the call was made with NOTHING on screen, so there is no project to
   * compare against and the answer is its caller's.
   *
   * Comparing against the signal at ARRIVAL was the R9 bug: a page that switched project while the
   * read was out refused the legitimate body of the project that asked, and a reset in that window
   * dropped the check entirely because the signal had gone back to `null`.
   */
  private bodyOfRequestedProject(
    revision: PolicyRevisionDto,
    expectedProjectId: string | null,
  ): PolicyRevisionDto {
    if (expectedProjectId !== null && revision.project_id !== expectedProjectId) {
      throw new ApiRequestError(FOREIGN_REVISION_FAILURE);
    }
    return revision;
  }

  /**
   * Publishes the textarea's text (`POST /v1/projects/{p}/policies`, AC1/AC4/AC5).
   *
   * The local checks run before the request, in this order: a caller without `policy:write` is
   * `refused` with this store's own `policy_write_forbidden` (F5 — the control is absent from the DOM
   * already, so this repeat of the rule costs nothing and sends nothing); text that is not a JSON
   * object is `invalid_json`; and a WIRE body over 65 536 UTF-8 bytes is `too_large`, carrying that
   * size so the page can say how far over it is. The size is the compact body's, never the
   * pretty-printed text's (F1): indentation is what the editor shows, and refusing a body the server
   * accepts would be a bug of this tab's own making. None of the three sends anything. A second call
   * while one is in flight is `busy` — the route is not replayable in the way the reveal routes are,
   * and one click must not append two revisions.
   *
   * `If-Match` is {@link headRevisionId}: the head the LAST list answer named. A `412` answers
   * `conflict` with the head the server named and is NEVER retried — the caller opens the shared
   * conflict dialog, and only the user's explicit reload can move the attempt forward. Any other
   * refusal (a `422`, a `413`, a `429`, a transport failure, a cancellation) is `refused` with the
   * server's own error object, verbatim.
   *
   * On `created`/`unchanged` the answer is returned FIRST and the list re-read behind it, without
   * being awaited and without being able to fail this call: the revision exists either way, and the
   * head the screen shows must come from a server answer, never from this optimistic moment.
   *
   * Never rejects: the dialog awaits it from a click handler.
   */
  async publish(bodyText: string): Promise<PublishResult> {
    if (this.publishingSignal()) {
      return { kind: 'busy' };
    }
    if (!this.session.has('policy:write')) {
      return { kind: 'refused', error: NO_WRITE_FAILURE };
    }
    const projectId = this.projectIdSignal();
    if (projectId === null) {
      return { kind: 'refused', error: NO_SCOPE_FAILURE };
    }

    const body = parsePolicyObject(bodyText);
    if (body === null) {
      return { kind: 'invalid_json' };
    }
    // What the request would carry: the compact document, not the indented text on screen (F1). The
    // cap is declared in UTF-8 bytes, so a multi-byte character counts as every byte it occupies.
    // `null` is "no measurable wire body": the text is not JSON, or it is JSON the engine refuses to
    // write compactly (a pathological body, D2). Either way nothing is sent — refusing with the
    // no-body answer is the honest one, because a size invented for an unmeasurable body would be.
    const bytes = policyBodyBytes(bodyText);
    if (bytes === null) {
      return { kind: 'invalid_json' };
    }
    if (bytes > POLICY_BODY_MAX_BYTES) {
      return { kind: 'too_large', bytes };
    }

    // The head is read BEFORE the request: the `If-Match` this attempt quotes is the one the user
    // was shown, and a background refresh landing mid-flight must not rewrite it.
    const ifMatch = this.headRevisionIdSignal();
    this.publishingSignal.set(true);
    try {
      const published = await this.data.publish(projectId, body, ifMatch);
      const answer: PublishResult = {
        kind: published.created ? 'created' : 'unchanged',
        revision: published.revision,
      };
      // The list on screen still shows the old head, so it owes the screen a re-read — but not the
      // caller, who already holds the stored revision.
      this.refreshBehind(projectId);
      return answer;
    } catch (error: unknown) {
      return this.publishFailure(error);
    } finally {
      this.publishingSignal.set(false);
    }
  }

  /**
   * The activation seam: the page renders exactly what the server said, and nothing is inferred
   * from a revision's `state`. {@link PolicyProjectReads} calls it with the served object, and with
   * `null` for an absent, forbidden or failed read; nothing else ever writes it.
   */
  applyActivation(activation: PolicyActivationStateDto | null): void {
    this.activationSignal.set(activation);
  }

  /**
   * Forgets everything the view showed: the phase returns to `idle`, the rows, project, head and
   * activation are cleared, the body cache is dropped and any read still in flight is abandoned (its
   * generation is stale, so it writes nothing). The page calls this on destroy.
   *
   * {@link publishing} is deliberately NOT cleared: a request that is still out keeps its lock, so a
   * press arriving after the view is gone cannot append a second revision from one action.
   */
  reset(): void {
    this.generation += 1;
    this.clearState();
    this.bodyCache.clear();
    // The in-flight reads are abandoned with the view: a later call starts its own, which caches for
    // the view that exists (the abandoned one only answers the caller that asked before the reset).
    this.bodyReads.clear();
    // Nothing is open any more, so a reload has nothing to retry (F6).
    this.lastRequestedRevisionId = null;
    this.phaseSignal.set('idle');
  }

  /** The one read every entry point shares (open's list, reload, a publish's refresh). */
  private async readList(anchor: string, generation: number, foreground: boolean): Promise<void> {
    this.anchorRevisionId = anchor;
    this.followedCursors.clear();
    this.nextCursorSignal.set(null);
    this.pagesRead = 1;
    if (foreground) {
      this.phaseSignal.set('loading');
      this.loadErrorSignal.set(null);
    }

    const read = await settledRead(() => this.data.listRevisions(anchor));
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // A background refresh is not the caller's failure, and it must not cost the revision the
      // server just stored: the phase and the rows stay exactly as they are. But its refusal is not
      // nobody's either (F4) — the screen still shows a head that is no longer true, so the server's
      // own error is surfaced through the page's alert, without touching `phase` or the rows. The
      // next read that succeeds clears it.
      if (foreground) {
        this.settleFailure(read.error);
      } else if (isApiRequestError(read.error)) {
        this.loadErrorSignal.set(read.error.error);
      }
      return;
    }

    this.applyPage(read.value);
  }

  /** Writes one served page as the list, the cursor and the head the next publish quotes. */
  private applyPage(page: PolicyRevisionPage): void {
    this.revisionsSignal.set(page.items);
    this.nextCursorSignal.set(page.nextCursor);
    this.headRevisionIdSignal.set(page.headRevisionId);
    this.loadErrorSignal.set(null);
    this.phaseSignal.set('ready');
  }

  /** Stores one read body as the newest, dropping the oldest read past {@link MAX_CACHED_BODIES}. */
  private cacheBody(revision: PolicyRevisionDto): void {
    this.bodyCache.delete(revision.id);
    this.bodyCache.set(revision.id, revision);
    while (this.bodyCache.size > MAX_CACHED_BODIES) {
      const oldest = this.bodyCache.keys().next();
      if (oldest.done === true) {
        break;
      }
      this.bodyCache.delete(oldest.value);
    }
  }

  /**
   * A read of the revision or of its list failed: a refusal is the same `not_found` state as an
   * unknown id, anything else the server said is `error` with its own message, and a rejection that
   * is not the server's answer is `error` with no copy at all (the page then renders its generic
   * failure). Both reads classify identically on purpose: a `404` on either route must not tell the
   * user which of the two objects was missing.
   */
  private settleFailure(error: unknown): void {
    if (!isApiRequestError(error)) {
      this.phaseSignal.set('error');
      return;
    }
    if (REFUSAL_KINDS.includes(error.error.kind)) {
      this.phaseSignal.set('not_found');
      return;
    }
    this.loadErrorSignal.set(error.error);
    this.phaseSignal.set('error');
  }

  /** One publish rejection, classified into the two kinds a caller acts on differently. */
  private publishFailure(error: unknown): PublishResult {
    if (!isApiRequestError(error)) {
      return { kind: 'refused', error: UNRESOLVED_FAILURE };
    }
    const failure = error.error;
    if (failure.kind === 'precondition_failed') {
      // The server named the current head in `details`; the dialog shows it and offers a reload.
      return {
        kind: 'conflict',
        error: failure,
        currentRevisionId: conflictFromError(failure, NO_CONFLICT_FIELDS, NO_CONFLICT_FIELDS)
          .currentRevision,
      };
    }
    return { kind: 'refused', error: failure };
  }

  /**
   * Re-reads the first page WITHOUT the caller waiting for it, and without any way for it to fail
   * the caller: the read owns its generation (a newer read discards it) and swallows its own
   * failure. Skipped when the project on screen is no longer the one the publish addressed.
   */
  private refreshBehind(projectId: string): void {
    const anchor = this.anchorRevisionId;
    if (anchor === null || this.projectIdSignal() !== projectId) {
      return;
    }
    const generation = ++this.generation;
    // `readList` settles its own state; this catch only keeps a rejection from becoming an
    // unhandled one. Nothing here is the caller's failure.
    void this.readList(anchor, generation, false).catch(() => undefined);
  }

  /** The state every new read starts from; the generation is the caller's to bump. */
  private clearState(): void {
    this.revisionsSignal.set(NO_REVISIONS);
    this.nextCursorSignal.set(null);
    this.projectIdSignal.set(null);
    this.headRevisionIdSignal.set(null);
    this.loadErrorSignal.set(null);
    this.activationSignal.set(null);
    // The project's own reads belong to the view too: their generations are bumped there, so an
    // answer still on its way cannot report the project that just left the screen.
    this.reads.clear();
    this.anchorRevisionId = null;
    this.followedCursors.clear();
    this.pagesRead = 1;
  }

  /**
   * Starts the activation and head reads for the project on screen. Not awaited and never rejected:
   * a refused or slow read is a state of the summary, never a reason for the list to wait or fail.
   */
  private startReads(projectId: string): void {
    this.reads.start(projectId, (activation) => this.applyActivation(activation));
  }
}
