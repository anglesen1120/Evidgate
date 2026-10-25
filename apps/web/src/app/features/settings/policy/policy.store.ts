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
// 1. A revision id is the page's only key (S83 P2). The contract has NO read for a project's head
//    revision or its activation, so `open()` starts from a revision id, learns the PROJECT from the
//    revision itself (`revision.project_id`, never from the URL or the route scope), and reads the
//    revision list through that same id. An id that is not UUID-shaped is the "no revision
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
// 5. Publication checks before it sends (S83 P4). A non-object body and a body over 65 536 UTF-8
//    bytes are refused locally with no request; the server's closed schema is the authority on
//    everything else, so a `422` is surfaced VERBATIM. The textarea's text is a parameter, never
//    state: no signal, field, URL or log line of this store holds a policy body
//    (`no-secret-field-in-store` scans store fields for exactly this class of mistake).
// 6. A `412` is the end of an attempt. The caller gets `conflict` with the current head the server
//    named, and nothing is retried or resubmitted — the deliberate reload is the user's.
// 7. The caller gets the publish, the screen gets the refresh. A `created`/`unchanged` answer is
//    returned as soon as the server confirmed it; re-reading the list is a background read it
//    neither awaits nor can fail, because a slow or refused list must never cost the user the
//    revision the server has already stored.

import { computed, inject, Injectable, signal } from '@angular/core';
import type {
  PolicyActivationDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { PolicyDataService, type PolicyRevisionPage } from '../../../core/data/policy-data.service';
import { isApiRequestError, type ApiError, type ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { conflictFromError } from '../../../shared/ui/conflict/conflict-details';

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
 * The body cap of `policy-revision.v1` (contracts/schemas, AC4): 65 536 UTF-8 bytes are accepted,
 * one more byte is refused before any request. The server enforces the same number, so this is a
 * courtesy that saves a round trip, not a second source of truth.
 */
const POLICY_BODY_LIMIT_BYTES = 65_536;

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
 * answers it for another tenant's or another project's id), a `403` for a reason other than the
 * antiforgery token, and `403 csrf_failed`. All three become the same `not_found` phase, so the tab
 * cannot tell an id it may not see from an id that does not exist.
 */
const REFUSAL_KINDS: readonly ApiErrorKind[] = ['not_found', 'forbidden', 'csrf_failed'];

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

/** One read, settled so a rejection is inspected instead of thrown. */
type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}

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

  private readonly revisionsSignal = signal<readonly PolicyRevisionSummaryDto[]>(NO_REVISIONS);
  private readonly phaseSignal = signal<PolicyPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectIdSignal = signal<string | null>(null);
  private readonly headRevisionIdSignal = signal<string | null>(null);
  private readonly activationSignal = signal<PolicyActivationDto | null>(null);
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
   * The project's policy activation, as the seam {@link applyActivation} was handed. No route reads
   * it today (S83 P2), so it is `null` in production and the chip renders "unavailable".
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
  }

  /**
   * Re-reads the list from its first page at the current anchor — the explicit reload a `412`
   * conflict dialog offers, and the page's own retry. It settles `error`/`not_found` on a refusal
   * (so the dialog's reload path sees the failure) and never rejects.
   */
  async reload(): Promise<void> {
    const anchor = this.anchorRevisionId;
    if (anchor === null) {
      return;
    }
    const generation = ++this.generation;
    await this.readList(anchor, generation, true);
  }

  /**
   * One revision with its body, cached for the diff and the publish seed: at most
   * {@link MAX_CACHED_BODIES} bodies, the oldest dropped. A cached body is returned without a
   * request (a stored revision is immutable) and re-inserted so it counts as the newest.
   *
   * REJECTS with the server's `ApiRequestError` when the read is refused: both callers (the diff
   * and the publish dialog) have a failure state of their own, and swallowing the refusal here
   * would leave them seeding an editor with nothing.
   */
  async loadBody(revisionId: string): Promise<PolicyRevisionDto> {
    const cached = this.bodyCache.get(revisionId);
    if (cached !== undefined) {
      this.bodyCache.delete(revisionId);
      this.bodyCache.set(revisionId, cached);
      return cached;
    }
    const revision = await this.data.getRevision(revisionId);
    this.bodyCache.delete(revision.id);
    this.bodyCache.set(revision.id, revision);
    while (this.bodyCache.size > MAX_CACHED_BODIES) {
      const oldest = this.bodyCache.keys().next();
      if (oldest.done === true) {
        break;
      }
      this.bodyCache.delete(oldest.value);
    }
    return revision;
  }

  /**
   * Publishes the textarea's text (`POST /v1/projects/{p}/policies`, AC1/AC4/AC5).
   *
   * The local checks run before the request: more than 65 536 UTF-8 bytes is `too_large` (with the
   * size, so the page can say how far over it is), and text that is not a JSON object is
   * `invalid_json`. Neither sends anything. A second call while one is in flight is `busy` — the
   * route is not replayable in the way the reveal routes are, and one click must not append two
   * revisions.
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
    const projectId = this.projectIdSignal();
    if (projectId === null) {
      return { kind: 'refused', error: NO_SCOPE_FAILURE };
    }

    // The cap is declared in UTF-8 bytes, so it is measured in them: a multi-byte character counts
    // as the bytes it occupies, not as one character.
    const bytes = new TextEncoder().encode(bodyText).byteLength;
    if (bytes > POLICY_BODY_LIMIT_BYTES) {
      return { kind: 'too_large', bytes };
    }
    const body = parsePolicyObject(bodyText);
    if (body === null) {
      return { kind: 'invalid_json' };
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
   * The activation seam (S83 P2): the page renders exactly what the server said, and nothing is
   * inferred from a revision's `state`. An activation belongs to the project on screen, so
   * {@link open} and {@link reset} clear it; nothing else ever writes it.
   */
  applyActivation(activation: PolicyActivationDto | null): void {
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
      // A background refresh is not the caller's failure and has nothing to say: the revision the
      // server just stored stays on screen, and the next explicit read tells the rest.
      if (foreground) {
        this.settleFailure(read.error);
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
    this.anchorRevisionId = null;
    this.followedCursors.clear();
    this.pagesRead = 1;
  }
}
