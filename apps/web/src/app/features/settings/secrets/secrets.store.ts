// 09.7.1-T3 (plan 09.7.1 §2 AC3, §4 D5, §5 "secret references" row, §6 ingest-token rotation
// states, §8 S3, §10 T3.1/T3.2; COORDINATOR DECISIONS S82 P1–P5): the Settings > Secrets tab's
// state, as a plain Angular signals service — private `signal`s, public `asReadonly()`/`computed`,
// no writable state escaping the class — shaped exactly like `members/members.store.ts`, because
// the workspace does not depend on `@ngrx/signals`.
//
// One instance per page (`providers: [SecretsStore]` on the page), because the store owns the
// cursor of one project's list and a project/project-scope switch must not inherit another list.
//
// Six rules shape every method:
//
// 1. Metadata only (AC3, S3). `IngestTokenView` is metadata by contract — `name`, `scopes`,
//    `prefix`, `status`, the timestamps — and the state holds nothing else. The rotation's
//    one-time plaintext is a RETURN VALUE of `rotate()`, destined for the component-scoped reveal
//    slot and never written to a signal, a log or a telemetry attribute. The state-shape spec
//    (`secrets.store.spec.ts`) enumerates the store's own field names and serialises every public
//    signal to prove it.
// 2. `status` IS the rotation state (S82 P3). The wire has no `rotation_state` or
//    `last_rotated_at`, so the store derives nothing from them and invents no substitute; the
//    reminder chip is the pure `rotationReminder` over `status` + `expires_at`.
// 3. The project is the scope (S82 P1). The page names a project; a generation counter means an
//    older project's read never writes, and the rows are cleared the moment the scope changes so
//    another project's tokens are never on screen under this project's heading.
// 4. The user reads pages, not a dump (00 §9.1, W7). The list follows `next_cursor` one page per
//    `loadMore()`, bounded by {@link MAX_CURSOR_PAGES} and refusing a cursor it already followed —
//    both contract violations throw a plain `Error`, never a partial list passed off as complete.
// 5. One rotation at a time (S82 P5). The route takes no `Idempotency-Key` (a one-time secret is
//    never replayed), so the guard cannot be a key: a second `rotate` while one is submitting is
//    ignored, which is what stops a double click from minting two successors. A `409`
//    (`invalid_transition`, `token_revoked`) is surfaced verbatim — the server decides, and no
//    client-side pre-check could drift from its guard. The same lock is published as `rotating`,
//    so the guard stays observable after `resetRotation()` cleared the published attempt.
// 6. A rejection that is not the server's answer always settles its own state, so the rotate
//    button can never be left submitting by a cancellation or a bug.
// 7. The caller gets the rotation, the screen gets the refresh. `rotate()` resolves with the
//    one-time value as soon as the server confirmed it; re-reading the list is a background read
//    it neither awaits nor can fail, because a slow or refused list must never cost the user the
//    secret the server has already minted. A refusal that proves the row on screen stale
//    (`409 invalid_transition`/`token_revoked`) starts the same background read for the same
//    reason: the Rotate button must not stay on a row that can never rotate again — and so does a
//    success answer with no usable secret, because the token did rotate either way.
//
// 8. CI-binding metadata is a SECOND, independent track (09.7.1-F15; plan §4
//    `.../settings/secrets/` row, §2 AC3; COORDINATOR DECISIONS S86 P1–P5). The Secrets tab shows
//    which repositories a project's CI is bound to, as metadata only: `CiBindingView` carries the
//    repository, the allowlists, the verification `status` and the timestamps, and the challenge
//    is only ever in the create answer, so no field or signal here can hold one. The track is
//    READ-ONLY — the contract has no `GET /ci-bindings/{b}` and the only writes are `PATCH`/`DELETE`,
//    so this store offers no create, update or disable method — and it is LIST-ONLY: one page per
//    read, bounded by the same {@link MAX_CURSOR_PAGES}, with `loadMoreCiBindings()` following
//    `next_cursor` and stopping (no request, `hasMoreCiBindings()` false) when the page names none.
//    Two servers' answers never duplicate a row on screen: a page is appended by `id`, so a row the
//    list already holds is dropped rather than rendered twice.
//
//    Its public surface, which the page codes against:
//
//      `ciBindings`            the rows, in server order, as many cursor pages as were followed
//      `ciBindingsPhase`       `idle` | `loading` | `ready` | `empty` | `forbidden` |
//                              `unavailable` | `error` (see {@link CiBindingsPhase})
//      `ciBindingsError`       the server's typed `ApiError`; `null` when the read never failed and
//                              when the rejection was not the server's answer at all
//      `ciBindingsProjectId`   the project the loaded rows belong to; `null` before the first read
//      `hasMoreCiBindings`     true while the server named another page
//      `loadCiBindings(p)`     reads the first page and becomes `ready`/`empty`; a project switch
//                              clears the rows BEFORE the read, so another project's bindings are
//                              never on screen under this project's heading
//      `loadMoreCiBindings()`  follows `next_cursor` once and appends, de-duplicated by `id`
//      `resetCiBindings()`     drops the track (rows, phase, error, cursor, project)
//
//    Nothing is read on construction (S86 P3): the page calls `loadCiBindings(projectId)` behind
//    its own `project:read` gate, so a caller that may not read this project never causes a
//    request, and a server 403 still renders `forbidden` for a session the client-side gate let
//    through. A token `load()` that switches the project drops this track too — the project is the
//    scope of the whole tab (rule 3) — unless the rows already belong to the project being loaded;
//    the reverse is not true, because the token read owns the scope and this track only follows it.
//    The track is dropped when the page (and therefore this per-page instance) is destroyed.

import { computed, inject, Injectable, signal } from '@angular/core';
import type { CiBindingView, IngestTokenCreatedResponse, IngestTokenView } from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';

/** The tab's read state: `idle` before the first read, which the page renders as loading. */
export type SecretsPhase = 'idle' | 'loading' | 'ready' | 'error';

/**
 * The CI-binding list's read state (09.7.1-F15), one phase per state the page renders:
 *
 * - `idle` — nothing asked yet; nothing is read on construction, so this is where the track starts
 *   and where {@link SecretsStore.resetCiBindings} returns it;
 * - `loading` — a read is out. A reload of rows already on screen keeps them, so the page can show
 *   the list while the refresh travels;
 * - `ready` — the server answered with at least one row (or a follow-up page was appended);
 * - `empty` — the server answered with no row at all: a project whose CI is not bound yet, which is
 *   a state of its own and not an error and not "loading forever";
 * - `forbidden` — the server refused the read with the `forbidden` kind: the caller may not see
 *   this project's bindings (403). A `csrf_failed` 403 is NOT this phase — the session may retry
 *   it — so it lands in `error` with its own failure. The page gates the section on `project:read`
 *   as a convenience; the server's answer is the authority, so its refusal is a state here rather
 *   than something the store pre-empts;
 * - `unavailable` — the rejection was NOT the server's answer (an adapter cancellation, a bug, a
 *   transport failure that never reached the mapper). The store refuses to invent copy, so
 *   {@link SecretsStore.ciBindingsError} stays `null` and the page renders its generic pattern;
 * - `error` — any other typed server failure (429, 503, 409, 422, 5xx), carried verbatim in
 *   `ciBindingsError` so the page can show the server's own message, code and retry delay.
 */
export type CiBindingsPhase =
  'idle' | 'loading' | 'ready' | 'empty' | 'forbidden' | 'unavailable' | 'error';

/**
 * The rotate action's state: `submitting` while the request is out, `done` once the server
 * answered with a successor, `failed` with the server's refusal verbatim.
 */
export interface RotationState {
  readonly phase: 'idle' | 'submitting' | 'failed' | 'done';
  /** The token this attempt rotated, so a row can render its own busy state; `null` when idle. */
  readonly tokenId: string | null;
  /** The server's typed failure, verbatim; `null` in every other phase. */
  readonly error: ApiError | null;
}

/**
 * What a successful rotation hands its CALLER — the one-time value included, because the reveal
 * slot is the only place it may go (S82 P3/P6, AC3). It is a return value and never state: no
 * signal, field or log line of the store ever holds it.
 */
export interface RotationResult {
  /** The successor's id, so the row can name what replaced the rotated token. */
  readonly successorId: string;
  /** When the rotated token stops verifying; `null` when the contract sent no overlap window. */
  readonly overlapEndsAt: string | null;
  /** The successor's plaintext, exactly as the server sent it, for the one-time reveal. */
  readonly value: string;
}

/** The frozen empty values, so an empty store and a cleared scope share one array identity. */
const NO_ROWS: readonly IngestTokenView[] = [];

/** The same frozen empty list for the CI-binding track (09.7.1-F15). */
const NO_CI_BINDINGS: readonly CiBindingView[] = [];

const IDLE_ROTATION: RotationState = { phase: 'idle', tokenId: null, error: null };

/**
 * The typed failure a rotation reports when the rejection is not the server's answer — an adapter
 * cancellation or a bug. `unexpected` is the classification table's "no UI state for this",
 * `status: 0` says no response exists, and no text is taken from the thrown value (a thrown value
 * is not server copy, and its message may carry request data the store must not surface).
 *
 * Unlike the members store's copy this one is NOT retryable: the rotate route takes no caller-owned
 * `Idempotency-Key`, and the interceptor's per-request key is fresh on every attempt, so a retry is
 * not a replay — it mints another successor if the first request did land. The list has to be
 * re-read before a second rotation is offered, so the message states that the outcome is unknown
 * rather than naming an action: this kind renders no control of its own on the page.
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message:
    'The request did not complete, so nothing was confirmed; the token may or may not have rotated.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The typed failure a rotation reports when the answer carries no usable `secret` — an empty
 * string or a value that is not a string at all. The server did rotate (this is a success answer),
 * but the one-time value the reveal exists for is not in it, so there is nothing to show and
 * nothing to retry (a second request would mint a second successor): only a re-read can tell the
 * user what the token is now.
 *
 * It is a STORE-LOCAL failure, not a server envelope: `unexpected` is the classification table's
 * "no UI state for this", the code is spelled like the mapper's own local codes (never a
 * `contracts/openapi.yaml` code), and the message is the store's copy — no text is taken from the
 * answer, and the value it did carry is never quoted, logged or described. The message states the
 * fact and asks for no action: the page offers a control of its own per failure kind, and this kind
 * has none, so copy must not tell the user to press something that is not there.
 */
const UNUSABLE_ANSWER_FAILURE: ApiError = {
  kind: 'unexpected',
  // 200 is the route's only success status (contracts/openapi.yaml), and a body did arrive: the
  // response exists, it is the one-time value in it that does not.
  status: 200,
  code: 'rotation_answer_unusable',
  message:
    'The server rotated the token, but the answer carried no secret to show, so there is nothing to reveal.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The two refusals that prove the row on screen is stale rather than merely busy: both are the
 * contract's permanent end states of a token that will never rotate again. This is the one place
 * the store reads a server code, and it does not become a UI state of its own — the refusal still
 * lands in `rotateState().error` verbatim and renders as the same `failed` attempt; the code only
 * decides whether the list is re-read, which the server's answer has just proven out of date.
 */
const STALE_ROW_CODES: readonly string[] = ['invalid_transition', 'token_revoked'];

/**
 * One read, settled so a rejection is inspected instead of thrown.
 */
type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

/**
 * The page appended to the rows already on screen, de-duplicated by `id` (09.7.1-F15, S86 P4).
 *
 * A keyset cursor over `(created_at, id)` is stable, but a row can still arrive twice: a write
 * between two reads shifts the window, and the contract does not forbid a server repeating an
 * entry. Rendering it twice would give one binding two DOM rows, two keys and two versions of the
 * truth, so the second copy is dropped. The incoming page is de-duplicated against itself too.
 *
 * The existing array is returned unchanged when nothing new arrived, so a page that added no row
 * does not notify every consumer of the signal for nothing.
 */
function appendCiBindingRows(
  existing: readonly CiBindingView[],
  incoming: readonly CiBindingView[],
): readonly CiBindingView[] {
  const seen = new Set(existing.map((row) => row.id));
  const appended: CiBindingView[] = [];
  for (const row of incoming) {
    if (seen.has(row.id)) {
      continue;
    }
    seen.add(row.id);
    appended.push(row);
  }
  return appended.length === 0 ? existing : [...existing, ...appended];
}

/**
 * The phase a failed CI-binding read settles into (09.7.1-F15). The mapping is by the typed
 * failure's KIND, never by its message:
 *
 * - no typed failure at all — the rejection was not the server's answer — is `unavailable`: the
 *   store has nothing to report and refuses to invent copy;
 * - `forbidden` (403) is its own phase, because a section the caller may not read is a page state,
 *   not an error message;
 * - everything else the server answered (429 `rate_limited`, 503 `unavailable`, a 409/422/5xx) is
 *   `error`, carrying the server's own `ApiError` so the page can show its message and, where the
 *   contract declares one, its `Retry-After`.
 */
function ciBindingsFailurePhase(failure: ApiError | null): CiBindingsPhase {
  if (failure === null) {
    return 'unavailable';
  }
  return failure.kind === 'forbidden' ? 'forbidden' : 'error';
}

async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}

@Injectable()
export class SecretsStore {
  private readonly data = inject(SecretsDataService);

  private readonly rowsSignal = signal<readonly IngestTokenView[]>(NO_ROWS);
  private readonly phaseSignal = signal<SecretsPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectIdSignal = signal<string | null>(null);
  private readonly rotateStateSignal = signal<RotationState>(IDLE_ROTATION);

  /**
   * The cursors this store already asked for, so a server that hands one back a second time is a
   * contract violation instead of an endless list of the same rows.
   */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current project's list, the first page included; bounded by the page cap. */
  private pagesRead = 1;
  /** Bumped by every read; a read of an older generation never writes state. */
  private generation = 0;
  /**
   * Bumped by every `rotate` attempt and by `resetRotation`. A dialog that closed, or a newer
   * attempt, owns the rotate state, so an older round trip settles into nothing — its one-time
   * value is dropped rather than handed to a caller the page no longer shows.
   */
  private rotateAttempt = 0;
  /**
   * The double-submit lock, published as `rotating`. It is this flag, not `rotateState().phase`,
   * that decides whether a rotation may start: `resetRotation()` clears the PUBLISHED attempt
   * state (the dialog closed) while the request is still open, and a published-state guard would
   * then let a second press mint a second successor from a single user action. Only the request
   * settling releases this flag.
   */
  private readonly rotateInFlightSignal = signal(false);

  /**
   * The CI-binding track's own state (09.7.1-F15, header rule 8). It is deliberately a SECOND set of
   * signals rather than a field on the token rows: the two reads are independent routes with
   * independent cursors and independent failure states, so a refused binding read must not turn the
   * token list — the tab's other half — into an error, and vice versa.
   */
  private readonly ciBindingsRowsSignal = signal<readonly CiBindingView[]>(NO_CI_BINDINGS);
  private readonly ciBindingsPhaseSignal = signal<CiBindingsPhase>('idle');
  private readonly ciBindingsErrorSignal = signal<ApiError | null>(null);
  private readonly ciBindingsNextCursorSignal = signal<string | null>(null);
  private readonly ciBindingsProjectSignal = signal<string | null>(null);
  /** The binding cursors already followed, the same contract-violation guard as the token list. */
  private readonly followedCiBindingCursors = new Set<string>();
  /** Pages read for the current binding list, the first page included; page-capped as well. */
  private ciBindingsPagesRead = 1;
  /** Bumped by every binding read AND by {@link resetCiBindings}; older reads never write. */
  private ciBindingsGeneration = 0;

  /** The project's ingest tokens, in server order, as many cursor pages as have been followed. */
  readonly rows = this.rowsSignal.asReadonly();
  /** The read state the page renders: loading, the list, or the tab's error state. */
  readonly phase = this.phaseSignal.asReadonly();
  /** The server's typed failure for the token read; `null` when it never failed. */
  readonly loadError = this.loadErrorSignal.asReadonly();
  /** True while the server named another page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);
  /** The project the loaded list belongs to; `null` before the first load. */
  readonly projectId = this.projectIdSignal.asReadonly();
  /** The rotate action's state; a 409 `invalid_transition`/`token_revoked` lands here verbatim. */
  readonly rotateState = this.rotateStateSignal.asReadonly();
  /**
   * True while a rotate request is out, INCLUDING after {@link resetRotation} cleared the published
   * attempt state: the request is still open, so the guard that ignores a second `rotate()` is
   * still armed, and a caller that only sees `rotateState()` would read "idle" while a press is in
   * fact being ignored. It is false the moment the request settles — before the list refresh it
   * started, which is not a rotation of its own.
   */
  readonly rotating = this.rotateInFlightSignal.asReadonly();

  /**
   * The project's CI bindings, in server order, as many cursor pages as have been followed
   * (09.7.1-F15). Metadata only: `CiBindingView` never carries the challenge or its hash.
   */
  readonly ciBindings = this.ciBindingsRowsSignal.asReadonly();
  /** The binding list's read state: see {@link CiBindingsPhase} for each phase. */
  readonly ciBindingsPhase = this.ciBindingsPhaseSignal.asReadonly();
  /**
   * The server's typed failure for the binding read; `null` when it never failed, and when the
   * rejection was not the server's answer at all (then the phase is `unavailable`).
   */
  readonly ciBindingsError = this.ciBindingsErrorSignal.asReadonly();
  /** The project the loaded bindings belong to; `null` before the first read and after a reset. */
  readonly ciBindingsProjectId = this.ciBindingsProjectSignal.asReadonly();
  /** True while the server named another binding page, so the page's next control can be offered. */
  readonly hasMoreCiBindings = computed(() => this.ciBindingsNextCursorSignal() !== null);

  /**
   * Reads the named project's first token page and becomes `ready`.
   *
   * Switching the project clears the rows before the read starts: the previous project's tokens
   * must never be rendered under the new project's heading, not even for the duration of the
   * round trip. A reload of the SAME project keeps them, so a rotation's refresh does not blank the
   * list the user is looking at.
   *
   * The rotate state is also cleared, because a rotation belongs to the view that started it.
   *
   * A scope switch drops the CI-binding track as well, but ONLY when that track belongs to another
   * project: the project is the scope of the whole tab (rule 3), so p-1's bindings must not survive
   * under p-2's heading even if the page has not re-read them yet. The guard matters in the other
   * order — a page that reads the bindings of `p-2` FIRST and then calls `load('p-2')` keeps the
   * rows it has just loaded, because they already belong to the project being loaded.
   *
   * Never rejects: the page calls `void store.load(...)` from an effect, so a rejection would be an
   * unhandled promise rejection. A cancellation or a bug leaves the typed error `null`, and the tab
   * renders the generic error pattern rather than a message the server never sent.
   */
  async load(projectId: string): Promise<void> {
    if (this.projectIdSignal() !== projectId) {
      this.rowsSignal.set(NO_ROWS);
      this.nextCursorSignal.set(null);
      const bindingProjectId = this.ciBindingsProjectSignal();
      if (bindingProjectId !== null && bindingProjectId !== projectId) {
        this.resetCiBindings();
      }
    }
    this.resetRotation();
    await this.read(projectId);
  }

  /**
   * Follows `next_cursor` once and appends the page, so the screen keeps the rows it already shows
   * while the next one is on its way (00 §9.1 keyset cursor; never "load all").
   *
   * A cursor already followed, or a {@link MAX_CURSOR_PAGES}-th page that still names another one,
   * is a contract violation and throws a plain `Error` — the same two refusals as
   * `IdentityDataService`, so a broken cursor costs visibility of the bug, not a hung tab. A typed
   * server refusal is not a bug: it becomes `loadError` and the rows already read stay on screen
   * (the error state only takes over when there is nothing to show).
   */
  async loadMore(): Promise<void> {
    const projectId = this.projectIdSignal();
    const cursor = this.nextCursorSignal();
    if (projectId === null || cursor === null) {
      return;
    }
    if (this.followedCursors.has(cursor)) {
      throw new Error(`the ingest-token list repeated cursor "${cursor}"`);
    }
    if (this.pagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the ingest-token list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.generation;
    const read = await settledRead(() => this.data.listIngestTokens(projectId, cursor));
    // A newer read (another project, or a rotation's refresh) owns the state now: this page
    // belongs to a list nobody renders any more.
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      this.loadErrorSignal.set(read.error.error);
      if (this.rowsSignal().length === 0) {
        this.phaseSignal.set('error');
      }
      return;
    }

    this.followedCursors.add(cursor);
    this.pagesRead += 1;
    const page = read.value;
    this.rowsSignal.update((rows) => [...rows, ...page.items]);
    this.nextCursorSignal.set(page.next_cursor);
  }

  /**
   * Rotates one ingest token (`POST /v1/ingest-tokens/{t}/rotate`, AC3's rotate action;
   * `ingest_token:write`, S82 P5) and starts a background re-read of the project's list.
   *
   * The successor's plaintext is returned to the caller and nowhere else: the caller hands it to
   * the one-time reveal slot, which drops it when the dialog closes. `rotateState` only carries the
   * attempt's phase, the token id and the server's refusal.
   *
   * The one-time value is handed over as soon as the server confirms it — `rotateState` becomes
   * `done` and the call resolves without waiting for the list re-read (§ rule 7). A slow refresh, or
   * one the server refuses, cannot delay or fail the rotation: `null` here always means "no
   * successor to reveal", never "the list was slow".
   *
   * A second call while one request is still open is IGNORED (`null`) — including after
   * {@link resetRotation} cleared the published state: the route takes no caller-owned
   * `Idempotency-Key` (the interceptor's key is fresh per request), so two requests from one user
   * action would mint two successors. {@link rotating} is true for exactly that window, so the
   * ignored call is observable rather than silent.
   *
   * The refusal is the server's, verbatim: `409 invalid_transition` ("already rotated") and
   * `409 token_revoked` reach `rotateState().error` with their own message and code, and — because
   * either one proves the row on screen can never rotate again — they re-read the list behind the
   * refusal, so its Rotate button does not survive the answer.
   *
   * An answer that carries no usable `secret` (empty or not a string) is a failed rotation: the
   * successor exists, but nothing can be revealed, so no value reaches the caller and the failure
   * is the store's own {@link UNUSABLE_ANSWER_FAILURE} — never a code the server did not send, and
   * never the value itself. The list is re-read behind it, because the row on screen did rotate.
   *
   * Never rejects: the row's button awaits it from a click handler.
   *
   * @returns the one-time result when the server minted a successor and the project on screen is
   *   still the one this attempt rotated; `null` when a request is already open, when the store has
   *   no project yet, when the server refused, when the answer carried no usable secret, when the
   *   rejection was not the server's answer at all (reported as the generic `unexpected` failure so
   *   the button is never left submitting), or when the scope moved during the round trip (that
   *   value belongs to the project the user left, and its dialog is gone).
   */
  async rotate(tokenId: string): Promise<RotationResult | null> {
    if (this.rotateInFlightSignal()) {
      return null;
    }
    const projectId = this.projectIdSignal();
    if (projectId === null) {
      return null;
    }

    const attempt = ++this.rotateAttempt;
    this.rotateInFlightSignal.set(true);
    this.rotateStateSignal.set({ phase: 'submitting', tokenId, error: null });
    try {
      return await this.rotateAttempted(tokenId, projectId, attempt);
    } finally {
      this.rotateInFlightSignal.set(false);
    }
  }

  /** One rotation, from the request to the refusal; the caller owns the in-flight lock. */
  private async rotateAttempted(
    tokenId: string,
    projectId: string,
    attempt: number,
  ): Promise<RotationResult | null> {
    let created: IngestTokenCreatedResponse;
    try {
      created = await this.data.rotateIngestToken(tokenId);
    } catch (error) {
      // An abandoned attempt (the dialog closed, or the scope switched) settles into nothing: the
      // state it would write belongs to a view nobody is looking at any more.
      if (attempt !== this.rotateAttempt) {
        return null;
      }
      const failure = isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
      this.rotateStateSignal.set({ phase: 'failed', tokenId, error: failure });
      // The refusal stays verbatim; the read behind it only removes the Rotate button the server
      // has just proven can never succeed.
      if (STALE_ROW_CODES.includes(failure.code)) {
        this.refreshList(projectId);
      }
      return null;
    }

    if (attempt !== this.rotateAttempt) {
      return null;
    }

    // The successor was minted, but the one-time value is the whole point of the answer: without
    // a usable one there is nothing a reveal could show, so the attempt is a failure rather than a
    // "done" that hands an empty string to the dialog. The row on screen is stale all the same —
    // the token DID rotate — so it gets the same re-read a proving 409 gets.
    const value: unknown = created.secret;
    if (typeof value !== 'string' || value.length === 0) {
      this.rotateStateSignal.set({ phase: 'failed', tokenId, error: UNUSABLE_ANSWER_FAILURE });
      this.refreshList(projectId);
      return null;
    }

    this.rotateStateSignal.set({ phase: 'done', tokenId, error: null });

    const result: RotationResult = {
      successorId: created.token.id,
      overlapEndsAt: created.overlap_ends_at ?? created.token.overlap_ends_at ?? null,
      value,
    };

    // The list on screen still shows the rotated row, so it owes the screen a re-read — but not
    // the caller, who already holds the value the server minted. The read owns its generation, so
    // a newer read (a project switch, another rotation) discards it.
    this.refreshList(projectId);
    return result;
  }

  /**
   * Re-reads the named project's first page WITHOUT the caller waiting for it, and without any way
   * for it to fail the caller: the read owns its generation (a newer read discards it) and settles
   * its own error state, so a slow or refused refresh can never turn a confirmed rotation into a
   * failed one, nor delay the value the user is waiting for.
   *
   * Skipped when the project on screen is no longer the one the action addressed: the list under
   * another heading is not this project's to rewrite.
   */
  private refreshList(projectId: string): void {
    if (this.projectIdSignal() !== projectId) {
      return;
    }
    // `read` is async and settles its own state; this catch only keeps a rejection from becoming
    // an unhandled one. Nothing here is the caller's failure.
    void this.read(projectId).catch(() => undefined);
  }

  /**
   * Clears the published rotate state, so closing the reveal dialog (or the row menu) starts a fresh
   * attempt. An attempt still in flight is abandoned: it settles into nothing and its one-time value
   * is dropped rather than shown in a dialog that is no longer open.
   *
   * It clears the state only — NOT the in-flight lock. A request that is still open keeps holding
   * the lock, so a press that arrives after the dialog closed cannot mint a second successor from
   * the same user action: `rotateState()` reads `idle` while {@link rotating} stays true, which is
   * what makes that ignored press observable instead of silent.
   */
  resetRotation(): void {
    this.rotateAttempt += 1;
    this.rotateStateSignal.set(IDLE_ROTATION);
  }

  /**
   * Reads the named project's first CI-binding page (09.7.1-F15) and becomes `ready`, or `empty`
   * when the server answered with no row at all.
   *
   * Switching the project clears the rows BEFORE the read starts: another project's bindings must
   * never be rendered under this project's heading, not even for the duration of the round trip. A
   * reload of the SAME project keeps them, so a page effect that re-runs does not blank the list.
   *
   * Nothing reads on construction: this is the only entry point that starts a request, and the
   * page calls it behind its own `project:read` gate. The server stays the authority — a session
   * the gate let through but the server refuses still lands in the `forbidden` phase.
   *
   * Never rejects, for the same reason {@link load} does not: the page calls it from an effect, so
   * a rejection would be an unhandled one. A cancellation or a bug leaves the typed error `null`
   * and the phase `unavailable` rather than inventing copy the server never sent.
   */
  async loadCiBindings(projectId: string): Promise<void> {
    if (this.ciBindingsProjectSignal() !== projectId) {
      this.ciBindingsRowsSignal.set(NO_CI_BINDINGS);
      this.ciBindingsNextCursorSignal.set(null);
    }
    await this.readCiBindings(projectId);
  }

  /**
   * Follows the CI-binding `next_cursor` once and appends the page (09.7.1-F15), so the screen
   * keeps the rows it already shows while the next one is on its way (00 §9.1 keyset cursor; never
   * "load all").
   *
   * Three refusals, the same three as the token list's follow-up:
   *
   * - no project or no further page — including a page that named `null`, omitted `next_cursor` or
   *   named an empty string — is not an error and sends NO request: the list is complete, and
   *   `hasMoreCiBindings()` already reads false;
   * - a cursor already followed, or a {@link MAX_CURSOR_PAGES}-th page that still names another
   *   one, is a contract violation thrown as a plain `Error`: a broken cursor costs visibility of
   *   the bug, not a hung tab;
   * - a typed server refusal is NOT a bug: it becomes `ciBindingsError` (with the `forbidden` or
   *   `error` phase when nothing is on screen) and the rows already read stay put.
   *
   * A row the list already holds is dropped rather than appended twice (see
   * {@link appendCiBindingRows}), so the page can never show one binding under two keys.
   */
  async loadMoreCiBindings(): Promise<void> {
    const projectId = this.ciBindingsProjectSignal();
    const cursor = this.ciBindingsNextCursorSignal();
    if (projectId === null || cursor === null) {
      return;
    }
    if (this.followedCiBindingCursors.has(cursor)) {
      throw new Error(`the ci-binding list repeated cursor "${cursor}"`);
    }
    if (this.ciBindingsPagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the ci-binding list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.ciBindingsGeneration;
    const read = await settledRead(() => this.data.listCiBindings(projectId, cursor));
    // A newer read (another project, a reload, a reset) owns the state now: this page belongs to a
    // list nobody renders any more — including its rows, which must not be appended to the new one.
    if (generation !== this.ciBindingsGeneration) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      const failure = read.error.error;
      this.ciBindingsErrorSignal.set(failure);
      if (this.ciBindingsRowsSignal().length === 0) {
        this.ciBindingsPhaseSignal.set(ciBindingsFailurePhase(failure));
      }
      return;
    }

    this.followedCiBindingCursors.add(cursor);
    this.ciBindingsPagesRead += 1;
    const page = read.value;
    this.ciBindingsRowsSignal.update((rows) => appendCiBindingRows(rows, page.items));
    const nextCursor = page.next_cursor;
    this.ciBindingsNextCursorSignal.set(
      nextCursor === undefined || nextCursor === null || nextCursor.length === 0
        ? null
        : nextCursor,
    );
    this.ciBindingsPhaseSignal.set(this.ciBindingsRowsSignal().length === 0 ? 'empty' : 'ready');
  }

  /**
   * Drops the whole CI-binding track: rows, phase, typed error, cursor and the project they
   * belonged to, and abandons any read in flight (it settles into nothing, because a generation
   * bump owns the state). The page calls it when it leaves the project or tears its own view down;
   * the store instance itself dies with the page, so a destroyed page leaves nothing behind either.
   *
   * The token list and the rotate state are untouched: this is the binding track's reset only.
   */
  resetCiBindings(): void {
    this.ciBindingsGeneration += 1;
    this.ciBindingsRowsSignal.set(NO_CI_BINDINGS);
    this.ciBindingsPhaseSignal.set('idle');
    this.ciBindingsErrorSignal.set(null);
    this.ciBindingsNextCursorSignal.set(null);
    this.ciBindingsProjectSignal.set(null);
    this.followedCiBindingCursors.clear();
    this.ciBindingsPagesRead = 1;
  }

  /**
   * The one read every entry point shares (first load, project switch, rotation refresh). It owns
   * the generation, so whichever read was started last is the one that writes.
   */
  private async read(projectId: string): Promise<void> {
    const generation = ++this.generation;
    this.projectIdSignal.set(projectId);
    this.phaseSignal.set('loading');
    this.loadErrorSignal.set(null);
    this.followedCursors.clear();
    this.nextCursorSignal.set(null);
    this.pagesRead = 1;

    const first = await settledRead(() => this.data.listIngestTokens(projectId));
    // A newer read owns the state now.
    if (generation !== this.generation) {
      return;
    }
    if (!first.ok) {
      if (isApiRequestError(first.error)) {
        this.loadErrorSignal.set(first.error.error);
      }
      // A refresh that failed keeps the rows already on screen — they are this project's; a first
      // read of a scope that has none is the tab's error state.
      if (this.rowsSignal().length === 0) {
        this.rowsSignal.set(NO_ROWS);
        this.phaseSignal.set('error');
      } else {
        this.phaseSignal.set('ready');
      }
      return;
    }

    this.rowsSignal.set(first.value.items);
    this.nextCursorSignal.set(first.value.next_cursor);
    this.phaseSignal.set('ready');
  }

  /**
   * The one CI-binding read every entry point shares (`loadCiBindings`, a project switch). It owns
   * its own generation, so whichever binding read was started last is the one that writes, and a
   * `resetCiBindings()` in between discards every read that was in flight when it ran.
   */
  private async readCiBindings(projectId: string): Promise<void> {
    const generation = ++this.ciBindingsGeneration;
    this.ciBindingsProjectSignal.set(projectId);
    this.ciBindingsPhaseSignal.set('loading');
    this.ciBindingsErrorSignal.set(null);
    this.followedCiBindingCursors.clear();
    this.ciBindingsNextCursorSignal.set(null);
    this.ciBindingsPagesRead = 1;

    const first = await settledRead(() => this.data.listCiBindings(projectId));
    // A newer read (or a reset) owns the state now.
    if (generation !== this.ciBindingsGeneration) {
      return;
    }
    if (!first.ok) {
      const failure = isApiRequestError(first.error) ? first.error.error : null;
      this.ciBindingsErrorSignal.set(failure);
      // A refresh that failed keeps the rows already on screen — they are this project's; a first
      // read of a scope that has none is the binding section's own state (see CiBindingsPhase).
      this.ciBindingsPhaseSignal.set(
        this.ciBindingsRowsSignal().length === 0 ? ciBindingsFailurePhase(failure) : 'ready',
      );
      return;
    }

    const rows = appendCiBindingRows(NO_CI_BINDINGS, first.value.items);
    const nextCursor = first.value.next_cursor;
    this.ciBindingsRowsSignal.set(rows);
    this.ciBindingsNextCursorSignal.set(
      nextCursor === undefined || nextCursor === null || nextCursor.length === 0
        ? null
        : nextCursor,
    );
    this.ciBindingsPhaseSignal.set(rows.length === 0 ? 'empty' : 'ready');
  }
}
