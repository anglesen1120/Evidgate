import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { EvidenceRevision, RunAttemptSummary, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError, isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { RunDataService } from '../data/run-data.service';
import { toDecisionCandidate } from './decision/decision-mapper';
import { refusesAccess } from './decision/decision-refusal';
import type { DecisionViewModel } from './decision/decision-view-model';
import { manifestFromRun, UNAVAILABLE_MANIFEST, type ManifestSummary } from './manifest-summary';
import { toAttemptRow, type AttemptRow } from './attempts/attempt-summary';
import {
  newestRevision,
  REVISIONS_CURSOR_REPEATED,
  REVISIONS_PAGE_LIMIT,
  REVISIONS_PAGE_WITHOUT_NEW_ROWS,
  REVISIONS_WALK_REFUSALS,
  takeFreshRevisions,
  type RevisionsStatus,
} from './revision-walk';

/** Rows per attempts request: the contract's maximum, so a normal run is one read. */
export const ATTEMPTS_PAGE_LIMIT = 200;

export type AttemptsStatus = 'idle' | 'loading' | 'ready' | 'error';
export type RunDetailStatus = 'idle' | 'loading' | 'ready' | 'not-found' | 'forbidden' | 'error';
/** The decision read is its own region state: a failure there never becomes a run-page failure. */
export type DecisionStatus = 'idle' | 'loading' | 'ready' | 'error';
export type { RevisionsStatus } from './revision-walk';
interface RunDetailState {
  run: RunStateDto | null;
  manifest: ManifestSummary;
  status: RunDetailStatus;
  loadError: ApiError | null;
  /** `null` until the first decision answer lands: the region renders its loading state. */
  decision: DecisionViewModel | null;
  decisionStatus: DecisionStatus;
  decisionError: ApiError | null;
  /** A served `mode: shadow` body was refused; the notice outlives the read that produced it. */
  shadowIgnored: boolean;
  /** `null` until the first attempts page lands: the table renders loading/error/unavailable. */
  attempts: AttemptRow[] | null;
  attemptsStatus: AttemptsStatus;
  attemptsError: ApiError | null;
  /** The opaque `next_cursor` of the last page; `null` once the walk is complete or refused. */
  attemptsCursor: string | null;
  attemptsLoadingMore: boolean;
  /** Loaded revision pages in server order (newest first), or `null` before the first answer. */
  revisions: EvidenceRevision[] | null;
  revisionsStatus: RevisionsStatus;
  revisionsError: ApiError | null;
  /** The opaque `next_cursor` of the last revision page; `null` once every page is loaded. */
  revisionsCursor: string | null;
  revisionsLoadingMore: boolean;
  /**
   * The revision the evidence region is pinned to: an explicit URL pin, else the accepted live
   * decision's revision, else the newest frozen revision. `null` until one of those is known.
   */
  pinnedRevision: number | null;
}

const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: '',
  message: '',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
function toApiError(failure: unknown): ApiError {
  return isApiRequestError(failure) ? failure.error : UNRESOLVED_FAILURE;
}

/** The decision region with no acceptable live answer; its copy is neutral and carries no data. */
const UNAVAILABLE: DecisionViewModel = { kind: 'unavailable', cause: 'error' };

/**
 * The two ways a keyset walk reads itself into a loop: the server hands back the cursor it was
 * given, or a follow-up page adds no attempt that is not already loaded. Both end the walk with
 * a TYPED failure — a plain `Error` would reach `toApiError` as `UNRESOLVED_FAILURE` and leave
 * the panel with empty copy — and neither is retryable, because the same request would be
 * refused the same way. The cursor is dropped with the failure, so Load more cannot re-request it.
 */
const ATTEMPTS_CURSOR_REPEATED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'attempts_cursor_repeated',
  message: 'The attempts list returned the same page cursor again.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
const ATTEMPTS_PAGE_WITHOUT_NEW_ROWS: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'attempts_page_without_new_rows',
  message: 'The attempts list returned a follow-up page with no new attempts.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
const ATTEMPTS_WALK_REFUSALS: ReadonlySet<string> = new Set([
  ATTEMPTS_CURSOR_REPEATED.code,
  ATTEMPTS_PAGE_WITHOUT_NEW_ROWS.code,
]);

/**
 * The incoming page without the attempts already loaded, in page order, with the first copy
 * kept. A write between two keyset reads shifts the window, and a server repeating an entry is
 * not forbidden, so a row can arrive twice; a cursor cycle (`A → B → A`) serves a whole earlier
 * page again. Rendering it twice would give one attempt two rows. Every attempt_id seen —
 * loaded before this read or new here — is added to `seen`, which the caller keeps for the walk.
 */
function takeFreshAttempts(
  seen: Set<string>,
  incoming: readonly RunAttemptSummary[],
): RunAttemptSummary[] {
  const fresh: RunAttemptSummary[] = [];
  for (const attempt of incoming) {
    if (seen.has(attempt.attempt_id)) continue;
    seen.add(attempt.attempt_id);
    fresh.push(attempt);
  }
  return fresh;
}

/** Page-local, newest-read-wins. Neither a failed read nor route reuse retains old run data. */
export const RunDetailStore = signalStore(
  withState<RunDetailState>({
    run: null,
    manifest: UNAVAILABLE_MANIFEST,
    status: 'idle',
    loadError: null,
    decision: null,
    decisionStatus: 'idle',
    decisionError: null,
    shadowIgnored: false,
    attempts: null,
    attemptsStatus: 'idle',
    attemptsError: null,
    attemptsCursor: null,
    attemptsLoadingMore: false,
    revisions: null,
    revisionsStatus: 'idle',
    revisionsError: null,
    revisionsCursor: null,
    revisionsLoadingMore: false,
    pinnedRevision: null,
  }),
  withComputed((store) => ({
    hasMoreAttempts: computed(() => store.attemptsCursor() !== null),
    hasMoreRevisions: computed(() => store.revisionsCursor() !== null),
    /** The loaded row for the pin, or `null` while that revision is not on a loaded page. */
    selectedRevision: computed<EvidenceRevision | null>(() => {
      const pinned = store.pinnedRevision();
      if (pinned === null) return null;
      return (store.revisions() ?? []).find((revision) => revision.revision_no === pinned) ?? null;
    }),
    /** The highest `revision_no` on the loaded pages: the server order is newest first. */
    latestRevisionNo: computed<number | null>(
      () => newestRevision(store.revisions() ?? [])?.revision_no ?? null,
    ),
    /** Collection completeness of the newest known revision, for the header badge only. */
    latestRevisionCompleteness: computed<'complete' | 'incomplete' | null>(
      () => newestRevision(store.revisions() ?? [])?.completeness ?? null,
    ),
    /** A known revision above the pin: the banner is data, never an automatic switch. */
    newerRevisionExists: computed(() => {
      const pinned = store.pinnedRevision();
      const latest = newestRevision(store.revisions() ?? [])?.revision_no ?? null;
      return pinned !== null && latest !== null && latest > pinned;
    }),
  })),
  withMethods((store) => {
    const data = inject(RunsDataService);
    // The decision read goes through the feature service, which validates the stale-typed body
    // before any member is read (`RunDataService.getDecision`); the run and attempts stay on the
    // reference service every other read uses.
    const decisionData = inject(RunDataService);
    let generation = 0;
    let attemptsGeneration = 0;
    let decisionGeneration = 0;
    let revisionsGeneration = 0;
    /** `attempt_id`s already shown; only an accepted page updates it, `load()` clears it. */
    let loadedAttemptIds = new Set<string>();
    /** `revision_no`s already shown; only an accepted page updates it, `load()` clears it. */
    let loadedRevisionNumbers = new Set<number>();
    /**
     * The pin the URL asked for, kept apart from the rendered pin so a default choice can never be
     * mistaken for an explicit one. `null` means the URL carries no pin.
     */
    let explicitPin: number | null = null;
    /** How the rendered pin was chosen; once set, no later response may replace it. */
    let pinSource: 'url' | 'default' | null = null;
    /**
     * One decision read. The live slot is replaced as one accepted body (never field by field), a
     * refuted shadow candidate leaves it untouched, and a transient failure keeps whatever was
     * accepted. An access refusal is not transient: it erases the accepted body, because its
     * reasons and hashes would otherwise stay readable to a caller the server just refused.
     */
    async function readDecision(runId: string): Promise<void> {
      const read = ++decisionGeneration;
      patchState(store, { decisionStatus: 'loading', decisionError: null });
      try {
        const candidate = toDecisionCandidate(await decisionData.getDecision(runId));
        if (read !== decisionGeneration) return;
        if (candidate.kind === 'shadow') {
          // A shadow row is never a live answer: keep the accepted live body, or state that no
          // live decision exists, and disclose the refusal instead of inventing pending.
          patchState(store, {
            decision: store.decision() ?? { kind: 'unavailable', cause: 'shadow-ignored' },
            decisionStatus: 'ready',
            shadowIgnored: true,
          });
          settleDefaultPin();
          return;
        }
        patchState(store, {
          decision: candidate,
          decisionStatus: 'ready',
          shadowIgnored: false,
        });
        settleDefaultPin();
      } catch (failure: unknown) {
        if (read !== decisionGeneration) return;
        const error = toApiError(failure);
        patchState(store, {
          decision: refusesAccess(error) ? UNAVAILABLE : (store.decision() ?? UNAVAILABLE),
          decisionStatus: 'error',
          decisionError: error,
        });
        settleDefaultPin();
      }
    }
    /** One read of the attempts keyset: the first page replaces, a later page appends. */
    async function readAttempts(runId: string, cursor: string | null): Promise<void> {
      const read = ++attemptsGeneration;
      const first = cursor === null;
      patchState(
        store,
        first
          ? { attempts: null, attemptsStatus: 'loading', attemptsError: null, attemptsCursor: null }
          : { attemptsLoadingMore: true, attemptsError: null },
      );
      try {
        const page = await data.listAttempts(runId, {
          limit: ATTEMPTS_PAGE_LIMIT,
          ...(cursor === null ? {} : { cursor }),
        });
        if (read !== attemptsGeneration) return;
        // A cursor the server repeats would loop forever: refuse it as a typed failure.
        if (page.next_cursor !== null && page.next_cursor === cursor) {
          throw new ApiRequestError(ATTEMPTS_CURSOR_REPEATED);
        }
        const seen = new Set(first ? [] : loadedAttemptIds);
        const fresh = takeFreshAttempts(seen, page.items);
        // A follow-up page that adds no attempt yet promises another page cannot advance the walk:
        // refuse it the same way. An empty terminal page (no cursor) simply ends the walk.
        if (!first && fresh.length === 0 && page.next_cursor !== null) {
          throw new ApiRequestError(ATTEMPTS_PAGE_WITHOUT_NEW_ROWS);
        }
        loadedAttemptIds = seen;
        const rows = fresh.map(toAttemptRow);
        patchState(store, {
          attempts: first ? rows : [...(store.attempts() ?? []), ...rows],
          attemptsStatus: 'ready',
          attemptsCursor: page.next_cursor,
          attemptsLoadingMore: false,
        });
      } catch (failure: unknown) {
        if (read !== attemptsGeneration) return;
        const error = toApiError(failure);
        patchState(store, {
          attemptsStatus: first ? 'error' : 'ready',
          attemptsError: error,
          attemptsLoadingMore: false,
          // A refused walk ends here: the rows stay, and Load more goes away with the cursor.
          ...(ATTEMPTS_WALK_REFUSALS.has(error.code) ? { attemptsCursor: null } : {}),
        });
      }
    }
    /**
     * The first-load default, and the only place a pin is chosen without user intent: the accepted
     * live decision's revision, else the newest frozen revision. It runs after either read answers
     * and never after a pin exists, so a slower response cannot move a committed pin.
     */
    function settleDefaultPin(): void {
      if (pinSource !== null) return;
      const decision = store.decision();
      if (decision?.kind === 'evaluated') {
        pinSource = 'default';
        patchState(store, { pinnedRevision: decision.evidenceRevision });
        return;
      }
      const decisionSettled =
        store.decisionStatus() === 'ready' || store.decisionStatus() === 'error';
      const newest = store.latestRevisionNo();
      if (decisionSettled && newest !== null) {
        pinSource = 'default';
        patchState(store, { pinnedRevision: newest });
      }
    }
    /** A pin chosen by the visitor or by an explicit action: the URL records it next. */
    function pinTo(revision: number): void {
      if (!Number.isInteger(revision) || revision < 1) return;
      explicitPin = revision;
      pinSource = 'url';
      patchState(store, { pinnedRevision: revision });
    }
    /**
     * One read of the revision keyset: the first page replaces, a later page appends. The list has
     * its own region state, so its failure leaves the run, the decision and the attempts untouched.
     */
    async function readRevisions(runId: string, cursor: string | null): Promise<void> {
      const read = ++revisionsGeneration;
      const first = cursor === null;
      patchState(
        store,
        first
          ? {
              revisions: null,
              revisionsStatus: 'loading',
              revisionsError: null,
              revisionsCursor: null,
            }
          : { revisionsLoadingMore: true, revisionsError: null },
      );
      try {
        const page = await data.listEvidenceRevisions(runId, {
          limit: REVISIONS_PAGE_LIMIT,
          ...(cursor === null ? {} : { cursor }),
        });
        if (read !== revisionsGeneration) return;
        // The same two loop shapes the attempts walk refuses: a repeated cursor, and a follow-up
        // page that adds no revision while promising another.
        if (page.next_cursor !== null && page.next_cursor === cursor) {
          throw new ApiRequestError(REVISIONS_CURSOR_REPEATED);
        }
        const seen = new Set(first ? [] : loadedRevisionNumbers);
        const fresh = takeFreshRevisions(seen, page.items);
        // A follow-up page that adds no revision yet promises another page cannot advance the walk:
        // refuse it the same way. An empty terminal page (no cursor) simply ends the walk.
        if (!first && fresh.length === 0 && page.next_cursor !== null) {
          throw new ApiRequestError(REVISIONS_PAGE_WITHOUT_NEW_ROWS);
        }
        loadedRevisionNumbers = seen;
        patchState(store, {
          revisions: first ? fresh : [...(store.revisions() ?? []), ...fresh],
          revisionsStatus: 'ready',
          revisionsCursor: page.next_cursor,
          revisionsLoadingMore: false,
        });
        settleDefaultPin();
      } catch (failure: unknown) {
        if (read !== revisionsGeneration) return;
        const error = toApiError(failure);
        patchState(store, {
          revisionsStatus: first ? 'error' : 'ready',
          revisionsError: error,
          revisionsLoadingMore: false,
          ...(REVISIONS_WALK_REFUSALS.has(error.code) ? { revisionsCursor: null } : {}),
        });
      }
    }
    return {
      async load(runId: string): Promise<void> {
        const read = ++generation;
        ++attemptsGeneration;
        // A decision answer for the run this route just left must not land on the new one.
        ++decisionGeneration;
        ++revisionsGeneration;
        loadedAttemptIds = new Set<string>();
        loadedRevisionNumbers = new Set<number>();
        explicitPin = null;
        pinSource = null;
        patchState(store, {
          run: null,
          manifest: UNAVAILABLE_MANIFEST,
          status: 'loading',
          loadError: null,
          decision: null,
          decisionStatus: 'idle',
          decisionError: null,
          shadowIgnored: false,
          attempts: null,
          attemptsStatus: 'idle',
          attemptsError: null,
          attemptsCursor: null,
          attemptsLoadingMore: false,
          revisions: null,
          revisionsStatus: 'idle',
          revisionsError: null,
          revisionsCursor: null,
          revisionsLoadingMore: false,
          pinnedRevision: null,
        });
        // The decision and the revision list are read alongside the run rather than after it:
        // each failure belongs to its own region, so neither can hold the others back.
        const decisionRead = readDecision(runId);
        const revisionsRead = readRevisions(runId, null);
        try {
          const run = await data.get(runId);
          if (read !== generation) return;
          patchState(store, { run, manifest: manifestFromRun(run), status: 'ready' });
          void readAttempts(runId, null);
        } catch (failure: unknown) {
          if (read !== generation) return;
          const error = toApiError(failure);
          patchState(store, {
            run: null,
            manifest: UNAVAILABLE_MANIFEST,
            status:
              error.kind === 'not_found'
                ? 'not-found'
                : error.kind === 'forbidden'
                  ? 'forbidden'
                  : 'error',
            loadError: error,
          });
        }
        await Promise.all([decisionRead, revisionsRead]);
      },
      /**
       * Re-read only the decision, keeping the accepted body on screen while it is in flight.
       *
       * This is the refresh seam for the run-detail surface: the region's retry calls it, and a
       * future `evaluation.created` poll signal calls the same method rather than a second read.
       */
      async refreshDecision(): Promise<void> {
        const run = store.run();
        if (run !== null) await readDecision(run.run_id);
      },
      /** Re-read the first attempts page after a failure; the run itself is not re-read. */
      async retryAttempts(): Promise<void> {
        const run = store.run();
        if (run !== null) await readAttempts(run.run_id, null);
      },
      /** Follow `next_cursor` once; never loops, and refuses while a read is in flight. */
      async loadMoreAttempts(): Promise<void> {
        const run = store.run();
        const cursor = store.attemptsCursor();
        if (run === null || cursor === null || store.attemptsLoadingMore()) return;
        await readAttempts(run.run_id, cursor);
      },
      /**
       * Apply the URL's pin. An explicit positive revision always wins; its removal re-derives the
       * first-load default once, and a pin already chosen is never replaced by a slower response.
       */
      applyUrlPin(revision: number | null): void {
        if (typeof revision === 'number' && Number.isInteger(revision) && revision > 0) {
          if (pinSource === 'url' && explicitPin === revision) return;
          pinTo(revision);
          return;
        }
        if (pinSource !== 'url' || explicitPin === null) return;
        explicitPin = null;
        pinSource = null;
        patchState(store, { pinnedRevision: null });
        settleDefaultPin();
      },
      /** The visitor's selection, or "View latest revision"; the URL records it next. */
      pinRevision(revision: number): void {
        pinTo(revision);
      },
      /** "View latest revision" without writing the number: the newest known revision. */
      pinLatestRevision(): void {
        const latest = store.latestRevisionNo();
        if (latest !== null) pinTo(latest);
      },
      /** Re-read the first revision page after a failure; the pin and the other regions stay. */
      async retryRevisions(): Promise<void> {
        const run = store.run();
        if (run !== null) await readRevisions(run.run_id, null);
      },
      /** Follow the revision cursor once; never loops, and refuses while a read is in flight. */
      async loadOlderRevisions(): Promise<void> {
        const run = store.run();
        const cursor = store.revisionsCursor();
        if (run === null || cursor === null || store.revisionsLoadingMore()) return;
        await readRevisions(run.run_id, cursor);
      },
    };
  }),
);
