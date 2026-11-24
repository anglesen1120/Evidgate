import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { EvidenceRevision, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError, isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { takeKeysetPage } from '../../../shared/keyset-walk';
import { RunDataService } from '../data/run-data.service';
import { withRunRefresh } from '../actions/cancel/with-run-refresh.feature';
import { toDecisionCandidate } from './decision/decision-mapper';
import { refusesAccess } from './decision/decision-refusal';
import type { DecisionViewModel } from './decision/decision-view-model';
import { manifestFromRun, UNAVAILABLE_MANIFEST, type ManifestSummary } from './manifest-summary';
import { withRunDetailRelease } from './release/run-detail-release.feature';
import { toAttemptRow, type AttemptRow } from './attempts/attempt-summary';
import { isCanonicalRevisionNo } from './revision-pin';
import {
  newestRevision,
  REVISIONS_PAGE_LIMIT,
  REVISIONS_REFUSALS,
  REVISIONS_WALK_REFUSALS,
  type RevisionsStatus,
} from './revision-walk';

/** Attempts use the shared pager's default server page size. */
export const ATTEMPTS_PAGE_LIMIT = 20;

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
  /** Opaque next cursor for the currently accepted attempts page. */
  attemptsCursor: string | null;
  attemptsLoadingMore: boolean;
  attemptsPage: number;
  attemptsPageSize: number;
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

/** A repeated forward cursor must not leave an endless Next action. */
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
    attemptsPage: 1,
    attemptsPageSize: ATTEMPTS_PAGE_LIMIT,
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
  withRunDetailRelease(),
  withRunRefresh(),
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
    let attemptCursors: (string | null)[] = [null];
    let pendingAttempt: { page: number; cursor: string | null } | null = null;
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
    /** Commit the page only on success; a failed navigation keeps the previous rows and intent. */
    async function readAttempts(
      runId: string,
      target: { page: number; cursor: string | null },
    ): Promise<void> {
      if (store.attemptsLoadingMore()) return;
      const read = ++attemptsGeneration;
      pendingAttempt = target;
      const first = store.attempts() === null;
      const history = [...attemptCursors.slice(0, target.page - 1), target.cursor];
      patchState(store, {
        attemptsStatus: first ? 'loading' : 'ready',
        attemptsLoadingMore: true,
        attemptsError: null,
      });
      try {
        const page = await data.listAttempts(runId, {
          limit: store.attemptsPageSize(),
          ...(target.cursor === null ? {} : { cursor: target.cursor }),
        });
        if (read !== attemptsGeneration) return;
        if (page.next_cursor !== null && history.includes(page.next_cursor)) {
          throw new ApiRequestError(ATTEMPTS_CURSOR_REPEATED);
        }
        attemptCursors = history;
        pendingAttempt = null;
        patchState(store, {
          attempts: page.items.map(toAttemptRow),
          attemptsStatus: 'ready',
          attemptsPage: target.page,
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
          ...(error.code === ATTEMPTS_CURSOR_REPEATED.code ? { attemptsCursor: null } : {}),
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
      // The same canonical form the URL parser accepts: a number that is not a canonical decimal
      // (`1e21`, a float, `0`) never becomes a pin, whichever call site produced it.
      if (!isCanonicalRevisionNo(revision)) return;
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
        const step = takeKeysetPage({
          cursor,
          items: page.items,
          nextCursor: page.next_cursor,
          seen: loadedRevisionNumbers,
          keyOf: (revision) => revision.revision_no,
          refusals: REVISIONS_REFUSALS,
        });
        if (step.outcome === 'refused') throw new ApiRequestError(step.error);
        loadedRevisionNumbers = step.seen;
        patchState(store, {
          revisions: first ? step.fresh : [...(store.revisions() ?? []), ...step.fresh],
          revisionsStatus: 'ready',
          revisionsCursor: step.nextCursor,
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
        store.resetRelease();
        attemptCursors = [null];
        pendingAttempt = null;
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
          attemptsPage: 1,
          attemptsPageSize: ATTEMPTS_PAGE_LIMIT,
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
          const firstRelease = run.releases?.[0];
          if (firstRelease) void store.selectRelease(firstRelease.release_id);
          void readAttempts(runId, { page: 1, cursor: null });
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
      async restartAttempts(): Promise<void> {
        const run = store.run();
        if (run === null) return;
        ++attemptsGeneration;
        attemptCursors = [null];
        patchState(store, { attemptsLoadingMore: false });
        await readAttempts(run.run_id, { page: 1, cursor: null });
      },
      async retryAttempts(): Promise<void> {
        const run = store.run();
        if (run !== null && pendingAttempt !== null) await readAttempts(run.run_id, pendingAttempt);
      },
      async nextAttempts(): Promise<void> {
        const run = store.run();
        const cursor = store.attemptsCursor();
        if (run === null || cursor === null) return;
        await readAttempts(run.run_id, { page: store.attemptsPage() + 1, cursor });
      },
      async previousAttempts(): Promise<void> {
        const run = store.run();
        const page = store.attemptsPage() - 1;
        if (run === null || page < 1) return;
        await readAttempts(run.run_id, { page, cursor: attemptCursors[page - 1] ?? null });
      },
      async setAttemptsPageSize(size: number): Promise<void> {
        const run = store.run();
        if (run === null || ![20, 50, 100].includes(size) || size === store.attemptsPageSize())
          return;
        ++attemptsGeneration;
        attemptCursors = [null];
        patchState(store, {
          attempts: null,
          attemptsPage: 1,
          attemptsPageSize: size,
          attemptsCursor: null,
          attemptsLoadingMore: false,
        });
        await readAttempts(run.run_id, { page: 1, cursor: null });
      },
      /**
       * Apply the URL's pin. An explicit positive revision always wins; its removal re-derives the
       * first-load default once, and a pin already chosen is never replaced by a slower response.
       */
      applyUrlPin(revision: number | null): void {
        if (isCanonicalRevisionNo(revision)) {
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
