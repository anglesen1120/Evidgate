// Resume from project, sample run, evidence and policy activation. Credential inventory
// belongs to the optional token panel and is never a prerequisite for this read.
//
// Scope: every SDK call is made with the id the route supplied (`orgId`, `projectId`), and the
// project row the server answered with must carry that same `org_id`. A row of another org — a
// URL that guesses a project id — is treated as absent, so the wizard renders AC1's not-found
// pattern rather than another tenant's steps (00 §9.2: a foreign id is indistinguishable from an
// unknown one).
//
// Step 5 is derived from the project-keyed read `getPolicyActivation`
// (`GET /v1/projects/{p}/policy-activation`, 07.1.1 F18): `activation` carries the answer as one
// of three states — `active` with the server's `PolicyActivationStateDto`, `none` for the
// contract's single `404`, `forbidden` when the caller may not read it — and `done[5]` is true
// exactly for `active`, so the wizard claims an activation only from a row the server answered.
// A `forbidden` read is not a failed load: steps 1–4 are unaffected and step 5 simply stays
// un-derived, which is the panel's "you may not read this" state rather than a guessed `true` or
// a "nothing is active" the caller cannot know. Any other refusal rejects the whole load, because
// it says nothing about the activation and the wizard must not publish a step it did not read.

import { inject, Injectable } from '@angular/core';
import type { PolicyActivationStateDto, ProjectDto, RunStateDto, RunSummary } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, isApiRequestError } from '../../../core/http/api-error';
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
 * Step 5's input as one closed set (09.2.1-T5.2): what the project-keyed activation read answered.
 *
 * `active` carries the server's document unchanged, so a panel renders the activation it was
 * actually given; `none` is the contract's single `404` (never activated, unknown, soft-deleted or
 * another tenant's project — indistinguishable, 00 §9.2); `forbidden` is a read the caller may not
 * make, which is neither of the other two and must not be reported as one.
 */
export type ProgressActivation =
  | { readonly kind: 'active'; readonly state: PolicyActivationStateDto }
  | { readonly kind: 'none' }
  | { readonly kind: 'forbidden' };

/** What the wizard knows after one resume: the project and the five done-conditions. */
export interface OnboardingProgress {
  /** The project the wizard resumes on; `null` only when the org has none (step 1). */
  readonly project: ProjectDto | null;
  readonly done: StepsDone;
  /**
   * The project's policy activation as the read answered it. `done[5]` is true exactly when this
   * is `active`; `none` and `forbidden` both leave step 5 open, and they stay distinguishable so
   * step 5's panel can say which of the two it is instead of implying the other.
   */
  readonly activation: ProgressActivation;
  /** The oldest run of the project with its current evidence revision, if any. */
  readonly sampleRun: RunStateDto | null;
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
        // No project means no activation was read, and none can be active.
        activation: { kind: 'none' },
        sampleRun: null,
        currentStep: 1,
      };
    }

    const [runs, activation] = await Promise.all([
      this.data.listRuns(project.id, { limit: 1 }),
      this.readActivation(project.id),
    ]);
    const oldest = pickOldestRun(runs.items);
    const sampleRun = oldest === null ? null : await this.data.getRun(oldest.run_id);

    const done: StepsDone = {
      1: true,
      2: false, // Optional credential management is never a required completion gate.
      3: sampleRun !== null && STEP_THREE_DONE[sampleRun.state] === true,
      4: sampleRun !== null && sampleRun.current_evidence_revision_id !== null,
      // An activation the server answered is the only proof of step 5: a refused or absent read
      // leaves it open (the `forbidden` and `none` states say which).
      5: activation.kind === 'active',
    };

    return {
      project,
      done,
      activation,
      sampleRun,
      currentStep: deriveCurrentStep(done),
    };
  }

  /**
   * Step 5's read as the three states the wizard reports. `readPolicyActivation` already answers
   * `null` for the contract's single `404` (never activated / unknown / foreign / soft-deleted —
   * indistinguishable, 00 §9.2), so `null` is `none`. A `403` is `forbidden`: the caller may not
   * read the pointer, which is a state of its own and not a failed load — steps 1–4 stay as
   * derived and step 5 stays open. Every other refusal re-throws, so the load rejects rather than
   * publishing a step 5 it never read.
   */
  private async readActivation(projectId: string): Promise<ProgressActivation> {
    try {
      const state = await this.data.readPolicyActivation(projectId);
      return state === null ? { kind: 'none' } : { kind: 'active', state };
    } catch (cause) {
      if (isApiRequestError(cause) && cause.error.status === 403) {
        return { kind: 'forbidden' };
      }
      throw cause;
    }
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
