// 09.2.1-T5 (plan 09.2.1 §2 AC5, §5 "Step 5 Enable collection", §8 rows T98 and 00 §9.1
// idempotency, §10 T5.1/T5.2; 07.1.1-T3/T4; ruling T62): the wizard's step 5.
//
// What the step can and cannot know, in contract terms (the decision recorded for this slice):
//
// - `contracts/openapi.yaml` declares `POST /v1/projects/{p}/policies` (07.1.1-T3) and
//   `POST /v1/projects/{p}/policy-activation` (07.1.1-T4) and nothing to READ a project's policy
//   revision or its activation. So the preview is not a read of a stored body: it is the constant
//   default advisory policy in `./default-policy.ts`, shown read-only with the explicit "not gating
//   yet" statement AC5 requires. The missing read is queue row F18.
// - For the same reason step 5 is NEVER derivable-done: `OnboardingProgressService` reports
//   `activationReadable: false` and `step5_done = false` (plan 09.2.1 line 148), so a reload lands
//   on this step again. There is deliberately NO localStorage, NO session flag and no other local
//   memory of a completed enablement: the repeat confirm is what makes the reload safe (the server
//   answers `changed: false` and still audits exactly one activation per confirm intent). T5.2's
//   "reload restores enabled-and-collecting from server state" therefore stays OPEN until F18
//   ships — do not close it in this file by inventing a read.
// - Confirm is a two-call chain: append the default body (`createPolicyRevision`, no
//   `Idempotency-Key` — the content hash is its idempotency), then activate that revision in
//   `advisory` mode (`activatePolicy`, `Idempotency-Key` required). ONE intent covers the chain:
//   `createIntentRunner` mints the key on the first press and reuses it on every explicit retry and
//   on a double submit (which does not even start a second chain, because the runner refuses a
//   second attempt while one is running). The appended revision id is kept per intent, so a retry
//   after an activation failure does not append again; if it ever does re-append (a new intent), it
//   sends the identical constant body, which the server answers with the same revision.
// - `advisory` is the only mode: `enforcing` is refused `422` until 07.7.1 and the UI offers no
//   choice, so no mode control exists here at all.
// - `policy:write` is the permission (AC5: the lone org creator holds it through the T98
//   `release_approver` grant). Without it the confirm is DISABLED, never hidden, and the "ask your
//   release approver" copy appears — the server stays the authority, and a refusal it sends is
//   rendered as the typed error state, never as a success.
//
// The refusal state is the shared `app-error-pattern` (the one `ConnectCiPage` uses), NOT
// `app-request-state`: that wrapper swaps in the generic forbidden/not-found panels, which replace
// the server's own `message`, `code` and `request_id` with fixed copy. Here a 403 arrives because
// the user pressed confirm on a command the server refused — the plan's §8 rule is that the refusal
// is rendered verbatim, and the route-level 403 (a progress read that the user may not see at all)
// is already the shell's fixed forbidden panel. The heading level is 3, under the shell's step h2.
//
// The terminal panel renders only after a successful answer, from that answer alone: revision
// number (from the revision this page appended, matched by id), abbreviated hash, mode, activation
// instant. Every value is interpolated, never parsed as markup, and the only server-provided text on
// the panel is the hash, which `abbreviatePolicyHash` sanitises (bidi/control characters removed)
// before it abbreviates — so a right-to-left override cannot reorder the panel.

import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import type { PolicyActivationDto, PolicyRevisionDto } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import type { Intent } from '../../../core/http/intent';
import { createIntentRunner } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import { ErrorPatternComponent } from '../../../shared/ui/states';
import {
  DEFAULT_ADVISORY_POLICY,
  abbreviatePolicyHash,
  previewDefaultPolicy,
} from './default-policy';

/** The revision this page appended for the current intent, and the key it belongs to. */
interface AppendedRevision {
  readonly intentKey: string;
  readonly projectId: string;
  readonly revision: PolicyRevisionDto;
}

@Component({
  selector: 'app-onboarding-enable-collection',
  imports: [DatePipe, ErrorPatternComponent, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './enable-collection.page.html',
  styleUrls: ['../../../shared/ui/states/state-panel.css', './enable-collection.page.css'],
})
export class EnableCollectionPage {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
  private readonly data = inject(OnboardingDataService);
  private readonly session = inject(SessionStore);
  private readonly destroyRef = inject(DestroyRef);
  /**
   * The append that belongs to the current intent. Kept in per-visit component state (never in
   * storage) so a retry after an activation failure reuses the revision instead of appending again,
   * and so the terminal panel can name the revision number the activation's id alone does not carry.
   */
  private readonly appendedRevision = signal<AppendedRevision | null>(null);

  /** The read-only preview values; see `previewDefaultPolicy`. */
  protected readonly preview = previewDefaultPolicy(DEFAULT_ADVISORY_POLICY);

  /**
   * The confirm as one user intent: the chain runs under a single `Idempotency-Key`, and the runner
   * never retries on its own — the error state asks, the user decides.
   */
  protected readonly activation = createIntentRunner<PolicyActivationDto>((intent) =>
    this.confirmPolicy(intent),
  );

  protected readonly permitted = computed(() => this.session.has('policy:write'));
  protected readonly busy = computed(() => this.activation.status() === 'running');
  protected readonly failure = this.activation.error;
  protected readonly enabled = computed(() => this.activation.result() ?? null);
  /** A transport failure says nothing about whether the activation was recorded. */
  protected readonly responseUncertain = computed(() => {
    const kind = this.failure()?.kind;
    return kind === 'timeout' || kind === 'network';
  });
  /** Only a failure the contract calls retryable offers the same-intent retry action. */
  protected readonly retryEligible = computed(
    () => this.permitted() && this.activation.retryEligible(),
  );
  protected readonly enabledHash = computed(() => {
    const activation = this.enabled();
    return activation === null ? '' : abbreviatePolicyHash(activation.hash);
  });
  /** The revision number, only when the appended revision is the one that became active. */
  protected readonly enabledRevisionNo = computed<number | null>(() => {
    const activation = this.enabled();
    const appended = this.appendedRevision();
    return activation !== null &&
      appended !== null &&
      appended.revision.id === activation.policy_revision_id
      ? appended.revision.revision_no
      : null;
  });

  protected readonly intro = $localize`:@@onboarding.enable.intro:Enabling collection records a decision for every run of this project. It does not gate any merge yet.`;
  protected readonly previewHeading = $localize`:@@onboarding.enable.preview-heading:Default advisory policy`;
  protected readonly notGatingCopy = $localize`:@@onboarding.enable.not-gating:Not gating yet. This policy runs in advisory mode, so Evidgate records what it would have decided and never blocks a merge.`;
  protected readonly lateWindowLabel = $localize`:@@onboarding.enable.late-window:Late-evidence window`;
  protected readonly minutesSuffix = $localize`:@@onboarding.enable.minutes:minutes`;
  protected readonly requiredSuitesLabel = $localize`:@@onboarding.enable.required-suites:Required suites`;
  protected readonly requiredCasesLabel = $localize`:@@onboarding.enable.required-cases:Required cases`;
  protected readonly gatedBranchLabel = $localize`:@@onboarding.enable.gated-branch:Default branch gated`;
  protected readonly notSetLabel = $localize`:@@onboarding.enable.not-set:Not set`;
  protected readonly noneLabel = $localize`:@@onboarding.enable.none:None`;
  protected readonly yesLabel = $localize`:@@onboarding.enable.yes:Yes`;
  protected readonly noLabel = $localize`:@@onboarding.enable.no:No`;
  protected readonly confirmLabel = $localize`:@@onboarding.enable.confirm:Enable collection`;
  protected readonly confirmingLabel = $localize`:@@onboarding.enable.confirming:Enabling collection…`;
  protected readonly askApprover = $localize`:@@onboarding.enable.ask-approver:Ask your release approver to enable collection for this project.`;
  protected readonly uncertainLabel = $localize`:@@onboarding.enable.response-uncertain:The confirmation may already be recorded. Retrying is safe: it reuses the same idempotency key.`;
  protected readonly enabledHeading = $localize`:@@onboarding.enable.enabled-heading:Project enabled and collecting`;
  protected readonly changedCopy = $localize`:@@onboarding.enable.changed:This project now collects evidence in advisory mode.`;
  protected readonly unchangedCopy = $localize`:@@onboarding.enable.unchanged:This project was already enabled in advisory mode, so this confirmation changed nothing on the server.`;
  protected readonly modeLabel = $localize`:@@onboarding.enable.mode:Mode`;
  protected readonly advisoryModeLabel = $localize`:@@onboarding.enable.mode-advisory:Advisory`;
  protected readonly revisionLabel = $localize`:@@onboarding.enable.revision:Policy revision`;
  protected readonly hashLabel = $localize`:@@onboarding.enable.hash:Revision hash`;
  protected readonly activatedAtLabel = $localize`:@@onboarding.enable.activated-at:Activated (UTC)`;

  constructor() {
    // A scope change abandons the intent and the appended revision: the next confirm belongs to a
    // different project, so its key must not be the old one. `reset()` also invalidates a running
    // attempt (its settlement then writes nothing), which is the guard a data-service call cannot
    // provide on its own.
    effect(() => {
      this.orgId();
      this.projectId();
      this.appendedRevision.set(null);
      this.activation.reset();
    });
    this.destroyRef.onDestroy(() => this.activation.reset());
  }

  /** The confirm action, once per user intent (guarded by the runner while it runs). */
  protected async confirm(): Promise<void> {
    if (this.busy() || !this.permitted()) return;
    await this.activation.run();
  }

  /** An explicit retry of the same intent: same key, same appended revision. */
  protected async retry(): Promise<void> {
    if (!this.retryEligible()) return;
    await this.activation.retry();
  }

  /**
   * The activation's mode. The wizard only ever requests `advisory` (`enforcing` is refused `422`
   * until 07.7.1), so `advisory` has copy and any other closed-enum value is shown as the wire name
   * the answer carried rather than as a translated label claiming a choice this page does not offer.
   */
  protected modeText(mode: PolicyActivationDto['mode']): string {
    return mode === 'advisory' ? this.advisoryModeLabel : mode;
  }

  /**
   * Append the default body, then activate it — both under `intent`.
   *
   * The project is captured once so a scope change mid-flight cannot split the chain across two
   * projects. The append is skipped when this intent already produced a revision (the retry path);
   * otherwise the constant body is sent unchanged, which is what makes a repeat append idempotent
   * by content hash.
   */
  private async confirmPolicy(intent: Intent): Promise<PolicyActivationDto> {
    const projectId = this.projectId();
    const revision = await this.appendDefaultPolicy(projectId, intent);
    return this.data.activatePolicy(projectId, revision.id, intent);
  }

  private async appendDefaultPolicy(projectId: string, intent: Intent): Promise<PolicyRevisionDto> {
    const appended = this.appendedRevision();
    if (appended !== null && appended.intentKey === intent.idempotencyKey) {
      return appended.revision;
    }
    const revision = await this.data.createPolicyRevision(projectId, DEFAULT_ADVISORY_POLICY);
    // Only a resident page of the same project keeps the revision: a late answer must not repopulate
    // the cache the scope effect just cleared.
    if (!this.destroyRef.destroyed && this.projectId() === projectId) {
      this.appendedRevision.set({ intentKey: intent.idempotencyKey, projectId, revision });
    }
    return revision;
  }
}
