// 09.2.1-T1.2 (plan 09.2.1 §6 "Wizard step derivation", §10 T1.2; T63; decisions (b) and (d)):
// the wizard's resumable position, derived from what the server holds and never from a local
// flag. One `load` answers the project the wizard resumes on plus the five done-conditions of
// `step-derivation.ts`.
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
import type { ProjectDto, RunStateDto, RunSummary } from '@evidgate/sdk';

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
   * How many CI bindings / ingest tokens the answer carried. Both reads ask for one row, so each
   * is `0` or `1`: the wizard needs presence (step 2's done-condition), not a total, and a wizard
   * resume must not page a tenant's whole token history.
   */
  readonly bindingCount: number;
  readonly tokenCount: number;
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
        bindingCount: 0,
        tokenCount: 0,
        currentStep: 1,
      };
    }

    // Issued together: the array is built before `Promise.all` subscribes, so all three requests
    // are in flight while the first one is still pending.
    const [bindings, tokens, runs] = await Promise.all([
      this.data.listCiBindings(project.id, { limit: 1 }),
      this.data.listIngestTokens(project.id, { limit: 1 }),
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
      bindingCount: bindings.items.length,
      tokenCount: tokens.items.length,
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
