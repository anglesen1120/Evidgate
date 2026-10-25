// 09.2.1-T1.2/T2 (plan 09.2.1 §6 "Wizard step derivation", §10 T1.2/T2; T63; decisions (b) and
// (d); AC2, AC7): the wizard's resumable position, derived from what the server holds and never
// from a local flag. One `load` answers the project the wizard resumes on, the step 2 metadata the
// panel renders (the CI bindings and ingest tokens the project already has, in server order) plus
// the five done-conditions of `step-derivation.ts`.
//
// Cost and ordering:
//   - the project resolves first, because every later read is project-scoped; a route that names
//     one is read with a single `getProject` (`GET /v1/projects/{p}`), never by walking the org's
//     project list;
//   - the three project reads (bindings, tokens, oldest run) are issued together, so a resume
//     costs one round trip after the project instead of three;
//   - `getRun` follows `listRuns`, because its run id is what the list answered (step 4 reads
//     `current_evidence_revision_id`, which the list row does not carry).
//
// Step 2's two lists are read with `CONNECT_LIST_LIMIT` rows: step 2's panel shows the rows it
// created or resumed with (T2.2: metadata only — neither list carries a secret), and the health
// panel derives its state from those rows (09.2.1-T7), while the done-condition stays presence —
// `done[2]` is "either list is not empty", exactly as T1 shipped it — so a token without a
// binding (or a binding without a token) completes step 2 either way (AC2). Because the
// panel renders one page, each list also reports whether the server held more rows than that page
// (`bindingsTruncated` / `tokensTruncated`: the response carried a `next_cursor`), so the panel can
// say the rows shown are not all of them instead of implying the project has exactly these.
//
// Scope: every SDK call is made with the id the route supplied (`orgId`, `projectId`), and the
// project row the server answered with must carry that same `org_id`. A row of another org — a
// URL that guesses a project id — is treated as absent, so the wizard renders AC1's not-found
// pattern rather than another tenant's steps (00 §9.2: a foreign id is indistinguishable from an
// unknown one).
//
// Step 5 is deliberately not derivable: the contract has `POST /v1/projects/{p}/policy-activation`
// and no project-keyed GET of it (07 §4, decisions (b)). `activationReadable` is therefore always
// `false` and `done[5]` is always `false`, so the wizard never claims an activation it has not
// seen; that is recorded as an open item rather than guessed `true`.

import { inject, Injectable } from '@angular/core';
import type {
  CiBindingView,
  IngestTokenView,
  ProjectDto,
  RunStateDto,
  RunSummary,
} from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import {
  deriveCurrentStep,
  NO_STEPS_DONE,
  type StepsDone,
  type WizardStep,
} from './step-derivation';

/**
 * The one run state pair that completes step 3 (00 §8.1 terminal collection states, plan §6).
 * `canceled` is terminal too but completes nothing: the wizard offers a retry for it instead.
 */
const STEP_THREE_DONE: Partial<Record<RunStateDto['state'], true>> = {
  complete: true,
  incomplete: true,
};

/**
 * The page size of step 2's two lists (`listCiBindings`, `listIngestTokens`), which is the
 * contract's own default: `Limit` in `contracts/openapi.yaml` is `default: 50`, `maximum: 200`,
 * and both list operations declare it, so naming it explicitly changes what the server answers
 * by nothing. The rows are rendered, not merely counted — the health panel derives its state from
 * them (09.2.1-T7) — so one page has to be the inventory the panel shows; a project that outgrows
 * it answers a `next_cursor`, which the progress reports as `bindingsTruncated`/`tokensTruncated`
 * instead of silently shortening the list. Exported so the two reads and the spec that pins them
 * agree on one value.
 */
export const CONNECT_LIST_LIMIT = 50;

/** What the wizard knows after one resume: the project and the five done-conditions. */
export interface OnboardingProgress {
  /** The project the wizard resumes on; `null` only when the org has none (step 1). */
  readonly project: ProjectDto | null;
  readonly done: StepsDone;
  /** Whether step 5's activation state could be read; always `false` (decision (b)). */
  readonly activationReadable: boolean;
  /** The oldest run of the project with its current evidence revision, if any. */
  readonly sampleRun: RunStateDto | null;
  /**
   * The project's CI bindings and ingest tokens, in the order the server answered them. Step 2's
   * panel renders these rows as metadata (a binding's `status`, a token's `prefix` and `status`);
   * neither list carries a secret, so resuming a project that has one of them shows what exists
   * without re-issuing anything (AC2, AC7). The reads ask for `CONNECT_LIST_LIMIT` rows — the
   * page a panel shows — because the rows are rendered, not merely counted, and step 2's health
   * panel derives its state from them (09.2.1-T7).
   */
  readonly bindings: readonly CiBindingView[];
  readonly tokens: readonly IngestTokenView[];
  /**
   * Whether the server held more rows than the page each list carries (`next_cursor` was not
   * `null`). Step 2's panel shows one page of rows, so it renders a notice when either list is
   * truncated rather than presenting that page as the project's whole inventory. Presence, not
   * truncation, is what completes step 2: `done[2]` stays "either list is not empty".
   */
  readonly bindingsTruncated: boolean;
  readonly tokensTruncated: boolean;
  readonly currentStep: WizardStep;
}

@Injectable({ providedIn: 'root' })
export class OnboardingProgressService {
  private readonly data = inject(OnboardingDataService);

  /**
   * Derives the wizard's position for `orgId` and the route's optional `projectId`. Rejections
   * are the gateway's typed `ApiRequestError` unchanged, so the store can map them to the fixed
   * forbidden / not-found / error patterns instead of publishing a wrong step.
   *
   * An explicit `projectId` the server refuses (`getProject` answers 404) keeps its not-found
   * meaning, and one whose row carries a different `org_id` is treated the same way: the URL
   * asked for a project, and answering "create your first project" would silently drop that ask.
   */
  async load(orgId: string, projectId: string | null): Promise<OnboardingProgress> {
    const project = await this.resolveProject(orgId, projectId);
    if (project === null) {
      if (projectId !== null) {
        throw notFoundProject();
      }
      return {
        project: null,
        done: NO_STEPS_DONE,
        activationReadable: false,
        sampleRun: null,
        bindings: [],
        tokens: [],
        // No project means neither list was read, so neither is truncated.
        bindingsTruncated: false,
        tokensTruncated: false,
        currentStep: 1,
      };
    }

    // Issued together: the array is built before `Promise.all` subscribes, so all three requests
    // are in flight while the first one is still pending.
    const [bindings, tokens, runs] = await Promise.all([
      this.data.listCiBindings(project.id, { limit: CONNECT_LIST_LIMIT }),
      this.data.listIngestTokens(project.id, { limit: CONNECT_LIST_LIMIT }),
      this.data.listRuns(project.id, { limit: 1 }),
    ]);
    const oldest = pickOldestRun(runs.items);
    const sampleRun = oldest === null ? null : await this.data.getRun(oldest.run_id);

    const done: StepsDone = {
      1: true,
      2: bindings.items.length + tokens.items.length > 0,
      3: sampleRun !== null && STEP_THREE_DONE[sampleRun.state] === true,
      4: sampleRun !== null && sampleRun.current_evidence_revision_id !== null,
      // No project-keyed read of the activation exists (decision (b)): never claimed as done.
      5: false,
    };

    return {
      project,
      done,
      activationReadable: false,
      sampleRun,
      bindings: bindings.items,
      tokens: tokens.items,
      // A non-null cursor is the server saying this page is not the whole list.
      bindingsTruncated: bindings.next_cursor !== null,
      tokensTruncated: tokens.next_cursor !== null,
      currentStep: deriveCurrentStep(done),
    };
  }

  /**
   * The project the wizard resumes on. Without a `projectId` it is the org's oldest project (the
   * server orders `(created_at, id)` ascending, so one row is the answer). With one, it is that
   * project read by id — one call, no list walk — and the `org_id` on the body must be this org's:
   * the server already answers a foreign id with 404 (00 §9.2), and the check repeats it so a body
   * that carried another tenant's org could never be adopted.
   */
  private async resolveProject(
    orgId: string,
    projectId: string | null,
  ): Promise<ProjectDto | null> {
    if (projectId === null) {
      const page = await this.data.listProjects(orgId, { limit: 1 });
      const oldest = page.items[0];
      return oldest !== undefined && oldest.org_id === orgId ? oldest : null;
    }
    const project = await this.data.getProject(projectId);
    return project.org_id === orgId ? project : null;
  }
}

/**
 * The oldest run of the page. The server already orders `(created_at, id)` ascending, so the first
 * row is normally it; choosing explicitly keeps the sample run stable if a page ever carries more
 * than one row, and an equal `created_at` keeps the cursor order (`<` not `<=`).
 */
function pickOldestRun(items: readonly RunSummary[]): RunSummary | null {
  let oldest: RunSummary | null = null;
  for (const item of items) {
    if (oldest === null || item.created_at < oldest.created_at) {
      oldest = item;
    }
  }
  return oldest;
}

/**
 * The refusal this service raises for a project the org does not list. It is shaped like the
 * gateway's typed failure so the store has one error contract to map; it carries no server text
 * and no request id, and the view renders the fixed not-found pattern rather than this message.
 */
function notFoundProject(): ApiRequestError {
  return new ApiRequestError({
    kind: 'not_found',
    status: 404,
    code: 'not_found',
    message: 'The project is not one this organization lists.',
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}
