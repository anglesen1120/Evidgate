import { computed, signal } from '@angular/core';
import type { CiBindingView } from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { settledRead } from './secrets-read';

/**
 * The CI-binding list's read state, one phase per state the page renders:
 *
 * - `idle` — nothing asked yet; nothing is read on construction, so this is where the track starts
 *   and where {@link SecretsCiBindingsTrack.reset} returns it;
 * - `loading` — a read is out. A reload of rows already on screen keeps them, so the page can show
 *   the list while the refresh travels;
 * - `ready` — the server answered with at least one row (or a follow-up page was appended);
 * - `empty` — the server answered with no row at all: a project whose CI is not bound yet, which is
 *   a state of its own and not an error and not "loading forever";
 * - `forbidden` — the server refused the read with the `forbidden` kind: the caller may not see
 *   this project's bindings (403). A `csrf_failed` 403 is NOT this phase — the session may retry
 *   it — so it lands in `error` with its own failure. The page gates the section on `project:read`
 *   as a convenience; the server's answer is the authority, so its refusal is a state here rather
 *   than something the track pre-empts;
 * - `unavailable` — the rejection was NOT the server's answer (an adapter cancellation, a bug, a
 *   transport failure that never reached the mapper). The track refuses to invent copy, so
 *   {@link SecretsCiBindingsTrack.error} stays `null` and the page renders its generic pattern;
 * - `error` — any other typed server failure (429, 503, 409, 422, 5xx), carried verbatim in
 *   {@link SecretsCiBindingsTrack.error} so the page can show the server's own message, code and
 *   retry delay.
 */
export type CiBindingsPhase =
  'idle' | 'loading' | 'ready' | 'empty' | 'forbidden' | 'unavailable' | 'error';

/** The frozen empty list, so a reset and a fresh track share one array identity. */
const NO_CI_BINDINGS: readonly CiBindingView[] = [];

/**
 * The page appended to the rows already on screen, de-duplicated by `id`.
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
 * The phase a failed CI-binding read settles into. The mapping is by the typed failure's KIND,
 * never by its message:
 *
 * - no typed failure at all — the rejection was not the server's answer — is `unavailable`: the
 *   track has nothing to report and refuses to invent copy;
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

/** The cursor to store: absent, `null` and the empty string all mean "this list is complete". */
function storedCursor(nextCursor: string | null | undefined): string | null {
  if (nextCursor === undefined || nextCursor === null || nextCursor.length === 0) {
    return null;
  }
  return nextCursor;
}

/**
 * The CI-binding list's own state: rows, phase, typed error, cursor, project and the generation
 * that decides which read wins.
 *
 * It is deliberately a SECOND set of signals rather than a field on the token rows: the two reads
 * are independent routes with independent cursors and independent failure states, so a refused
 * binding read must not turn the token list — the tab's other half — into an error, and vice versa.
 * The track is READ-ONLY (the contract offers no `GET /ci-bindings/{b}`) and LIST-ONLY (one page
 * per read), and nothing reads on construction: the page starts the first read behind its own
 * `project:read` gate.
 */
export class SecretsCiBindingsTrack {
  private readonly rowsSignal = signal<readonly CiBindingView[]>(NO_CI_BINDINGS);
  private readonly phaseSignal = signal<CiBindingsPhase>('idle');
  private readonly errorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectSignal = signal<string | null>(null);
  /** The binding cursors already followed, the same contract-violation guard as the token list. */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current binding list, the first page included; page-capped as well. */
  private pagesRead = 1;
  /** Bumped by every binding read AND by {@link reset}; older reads never write. */
  private generation = 0;

  /** The project's CI bindings, in server order, as many cursor pages as have been followed. */
  readonly rows = this.rowsSignal.asReadonly();
  /** The binding list's read state: see {@link CiBindingsPhase} for each phase. */
  readonly phase = this.phaseSignal.asReadonly();
  /**
   * The server's typed failure for the binding read; `null` when it never failed, and when the
   * rejection was not the server's answer at all (then the phase is `unavailable`).
   */
  readonly error = this.errorSignal.asReadonly();
  /** The project the loaded bindings belong to; `null` before the first read and after a reset. */
  readonly projectId = this.projectSignal.asReadonly();
  /** True while the server named another binding page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);

  constructor(private readonly data: SecretsDataService) {}

  /**
   * Reads the named project's first binding page and becomes `ready`, or `empty` when the server
   * answered with no row at all.
   *
   * Switching the project clears the rows BEFORE the read starts: another project's bindings must
   * never be rendered under this project's heading, not even for the duration of the round trip. A
   * reload of the SAME project keeps them, so a page effect that re-runs does not blank the list.
   *
   * Never rejects: the page calls it from an effect, so a rejection would be an unhandled one. A
   * cancellation or a bug leaves the typed error `null` and the phase `unavailable` rather than
   * inventing copy the server never sent.
   */
  async load(projectId: string): Promise<void> {
    if (this.projectSignal() !== projectId) {
      this.rowsSignal.set(NO_CI_BINDINGS);
      this.nextCursorSignal.set(null);
    }
    await this.read(projectId);
  }

  /**
   * Follows the `next_cursor` once and appends the page, so the screen keeps the rows it already
   * shows while the next one is on its way (never "load all").
   *
   * Three refusals, the same three as the token list's follow-up:
   *
   * - no project or no further page — including a page that named `null`, omitted `next_cursor` or
   *   named an empty string — is not an error and sends NO request: the list is complete, and
   *   {@link hasMore} already reads false;
   * - a cursor already followed, or a page cap that still names another one, is a contract
   *   violation thrown as a plain `Error`: a broken cursor costs visibility of the bug, not a hung
   *   tab;
   * - a typed server refusal is NOT a bug: it becomes the typed error (with the `forbidden` or
   *   `error` phase when nothing is on screen) and the rows already read stay put.
   *
   * A row the list already holds is dropped rather than appended twice (see
   * {@link appendCiBindingRows}), so the page can never show one binding under two keys.
   */
  async loadMore(): Promise<void> {
    const projectId = this.projectSignal();
    const cursor = this.nextCursorSignal();
    if (projectId === null || cursor === null) {
      return;
    }
    if (this.followedCursors.has(cursor)) {
      throw new Error(`the ci-binding list repeated cursor "${cursor}"`);
    }
    if (this.pagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the ci-binding list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.generation;
    const read = await settledRead(() => this.data.listCiBindings(projectId, cursor));
    // A newer read (another project, a reload, a reset) owns the state now: this page belongs to a
    // list nobody renders any more — including its rows, which must not be appended to the new one.
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      const failure = read.error.error;
      this.errorSignal.set(failure);
      if (this.rowsSignal().length === 0) {
        this.phaseSignal.set(ciBindingsFailurePhase(failure));
      }
      return;
    }

    this.followedCursors.add(cursor);
    this.pagesRead += 1;
    const page = read.value;
    this.rowsSignal.update((rows) => appendCiBindingRows(rows, page.items));
    this.nextCursorSignal.set(storedCursor(page.next_cursor));
    this.phaseSignal.set(this.rowsSignal().length === 0 ? 'empty' : 'ready');
  }

  /**
   * Drops the whole track: rows, phase, typed error, cursor and the project they belonged to, and
   * abandons any read in flight (it settles into nothing, because a generation bump owns the
   * state). The page calls it when it leaves the project or tears its own view down.
   *
   * The token list and the rotate state are untouched: this is the binding track's reset only.
   */
  reset(): void {
    this.generation += 1;
    this.rowsSignal.set(NO_CI_BINDINGS);
    this.phaseSignal.set('idle');
    this.errorSignal.set(null);
    this.nextCursorSignal.set(null);
    this.projectSignal.set(null);
    this.followedCursors.clear();
    this.pagesRead = 1;
  }

  /**
   * The one read every entry point shares (`load`, a project switch). It owns the generation, so
   * whichever binding read was started last is the one that writes, and a {@link reset} in between
   * discards every read that was in flight when it ran.
   */
  private async read(projectId: string): Promise<void> {
    const generation = ++this.generation;
    this.projectSignal.set(projectId);
    this.phaseSignal.set('loading');
    this.errorSignal.set(null);
    this.followedCursors.clear();
    this.nextCursorSignal.set(null);
    this.pagesRead = 1;

    const first = await settledRead(() => this.data.listCiBindings(projectId));
    // A newer read (or a reset) owns the state now.
    if (generation !== this.generation) {
      return;
    }
    if (!first.ok) {
      const failure = isApiRequestError(first.error) ? first.error.error : null;
      this.errorSignal.set(failure);
      // A refresh that failed keeps the rows already on screen — they are this project's; a first
      // read of a scope that has none is the binding section's own state (see CiBindingsPhase).
      this.phaseSignal.set(
        this.rowsSignal().length === 0 ? ciBindingsFailurePhase(failure) : 'ready',
      );
      return;
    }

    const rows = appendCiBindingRows(NO_CI_BINDINGS, first.value.items);
    this.rowsSignal.set(rows);
    this.nextCursorSignal.set(storedCursor(first.value.next_cursor));
    this.phaseSignal.set(rows.length === 0 ? 'empty' : 'ready');
  }
}
