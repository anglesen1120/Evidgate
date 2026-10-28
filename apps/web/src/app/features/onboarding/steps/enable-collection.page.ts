// 09.2.1-T5 (plan 09.2.1 §2 AC5, §5 "Step 5 Enable collection", §8 rows T98 and 00 §9.1
// idempotency, §10 T5.1/T5.2; 07.1.1-T3/T4; ruling T62): the wizard's step 5.
//
// The page reads the project's activation on entry and on every org/project scope change. A 200
// restores the terminal panel without a command; only a 404 (the data service's null) permits the
// default-policy confirm. A forbidden read keeps the preview read-only, and every other failure
// uses the shared error panel. OnboardingProgressService derives done[5] from the same server read;
// there is no localStorage, session flag or cookie memory of completion.
//
// The default preview and create-then-activate chain remain interim until F18 addendum 2 moves
// default-policy creation server-side. One intent retains its activation key and appended revision
// through explicit retries; the runner never retries or starts a second in-flight chain itself.
// A confirm can replace only a policy activated after the absent-activation read. Its warning is
// always described by the button. policy:write remains required, with ask-approver copy otherwise.
//
// Read and command answers render their actual mode; only advisory gets the not-gating copy.
// Terminal metadata is interpolated, ids are sanitised, and the hash is sanitised before shortening.
// The heading takes focus after either a successful read or confirm. Refusals use app-error-pattern
// so the typed server message, code and request id are preserved rather than replaced by fixed copy.

import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import type { PolicyActivationDto, PolicyActivationStateDto, PolicyRevisionDto } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { createIntentRunner } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import { ErrorPatternComponent } from '../../../shared/ui/states';
import {
  DEFAULT_ADVISORY_POLICY,
  activationInstantLabel,
  policyHashLabel,
  previewDefaultPolicy,
  sanitizeLabel,
  sanitizedIdentifier,
} from './default-policy';

/** The revision this page appended for the current intent, and the key it belongs to. */
interface AppendedRevision {
  readonly intentKey: string;
  readonly projectId: string;
  readonly revision: PolicyRevisionDto;
}

type ActivationRead =
  | { readonly kind: 'loading' }
  | { readonly kind: 'active'; readonly state: PolicyActivationStateDto }
  | { readonly kind: 'none' }
  | { readonly kind: 'forbidden' }
  | { readonly kind: 'error'; readonly error: ApiError };

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
  protected readonly activationRead = signal<ActivationRead>({ kind: 'loading' });
  private readGeneration = 0;
  protected readonly reading = computed(() => this.activationRead().kind === 'loading');
  protected readonly busy = computed(() => this.activation.status() === 'running');
  protected readonly failure = this.activation.error;
  protected readonly enabled = computed(() => {
    const read = this.activationRead();
    return this.activation.result() ?? (read.kind === 'active' ? read.state : null);
  });
  protected readonly readFailure = computed(() => {
    const read = this.activationRead();
    return read.kind === 'error' ? read.error : null;
  });
  /** A transport failure says nothing about whether the activation was recorded. */
  protected readonly responseUncertain = computed(() => {
    const kind = this.failure()?.kind;
    return kind === 'timeout' || kind === 'network';
  });
  /** Only a failure the contract calls retryable offers the same-intent retry action. */
  protected readonly retryEligible = computed(
    () => this.permitted() && this.activationRead().kind === 'none' && this.activation.retryEligible(),
  );
  /**
   * R5: a retryable failure whose `Retry-After` window is still open disables the confirm natively —
   * pressing it could only be a no-op (`run()` from a retryable failure waits), so the control must
   * not invite the press. The window belongs to the runner, not to this page.
   */
  protected readonly waitingForRetryAfter = computed(
    () =>
      this.activation.status() === 'failed' &&
      this.failure()?.retryable === true &&
      !this.activation.retryEligible(),
  );
  protected readonly confirmDisabled = computed(
    () =>
      this.busy() ||
      !this.permitted() ||
      this.activationRead().kind !== 'none' ||
      this.waitingForRetryAfter(),
  );
  /** The warning remains described even after acknowledgement removal (F18 addendum 1). */
  protected readonly confirmDescribedBy = computed(() => {
    const reasons = ['enable-replaces-warning'];
    if (!this.permitted()) reasons.push('enable-permission');
    if (this.activationRead().kind === 'forbidden') reasons.push('enable-activation-unreadable');
    return reasons.join(' ');
  });
  protected readonly advisoryMode = computed(() => this.enabled()?.mode === 'advisory');
  protected readonly failureView = computed(() => this.readFailure() ?? this.failure());
  /**
   * The terminal panel's sentence. Only this page's own confirm can report what changed, and its
   * answer is always the default policy in `advisory` mode; a read-only panel says where it came
   * from instead of claiming a change nobody made on this visit.
   */
  protected readonly terminalIntro = computed(() => {
    const confirmed = this.activation.result();
    if (confirmed === undefined || !this.advisoryMode()) {
      return this.activeReadCopy;
    }
    return confirmed.changed ? this.changedCopy : this.unchangedCopy;
  });
  /**
   * The shared error panel's retry, per failure: a failed read is re-read (it wrote nothing), while
   * a failed command retries the same intent only when the contract calls it retryable.
   */
  protected readonly retryDisabled = computed(() =>
    this.readFailure() !== null ? this.busy() : !this.retryEligible() || this.busy(),
  );  /**
   * A read is side-effect free, so reading it again is always safe — but the shared pattern renders
   * its Retry only for a failure the contract calls retryable, and a 401 or a 5xx is not one. Rather
   * than claim a retryable refusal, the page offers its own re-read action for exactly those.
   */
  protected readonly readRetryWithoutAction = computed(() => {
    const failure = this.readFailure();
    return failure !== null && !failure.retryable;
  });
  protected readonly enabledProject = computed(() => sanitizedIdentifier(this.enabled()?.project_id));
  protected readonly enabledRevision = computed(() =>
    sanitizedIdentifier(this.enabled()?.policy_revision_id),
  );
  protected readonly enabledBy = computed(() => sanitizedIdentifier(this.enabled()?.activated_by));
  /** F5: guarded members, so a malformed answer renders "unavailable" instead of throwing. */
  protected readonly enabledHash = computed(() => policyHashLabel(this.enabled()?.hash));
  protected readonly enabledInstant = computed(() =>
    activationInstantLabel(this.enabled()?.activated_at),
  );
  protected readonly enabledPreviousRevision = computed(() =>
    sanitizedIdentifier(this.activation.result()?.previous_policy_revision_id),
  );
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
  /** R3: the page renders the envelope's detail paths itself, so it renders them sanitised. */
  protected clean(value: string | null | undefined): string {
    return sanitizeLabel(value ?? '');
  }
  /** The terminal heading takes focus on success (see the focus effect in the constructor). */
  private readonly enabledHeadingRef = viewChild<ElementRef<HTMLElement>>('enabledHeadingTarget');

  protected readonly intro = $localize`:@@onboarding.enable.intro:Enabling collection records a decision for every run of this project. It does not gate any merge yet.`;
  protected readonly replacesActiveCopy = $localize`:@@onboarding.enable.replaces-active:Confirming activates the default advisory policy for this project. It can only replace a policy activated after this page read that the project had no activation.`;
  protected readonly activationUnreadable = $localize`:@@onboarding.enable.activation-unreadable:The project's activation state could not be read with your permissions. Confirmation is disabled.`;
  protected readonly readingLabel = $localize`:@@onboarding.enable.reading:Reading the project's activation…`;
  protected readonly activeReadCopy = $localize`:@@onboarding.enable.active-read:This project is enabled. This panel is built from the activation recorded on the server, so reloading the wizard shows the same state.`;
  protected readonly retryReadLabel = $localize`:@@onboarding.enable.retry-read:Read the activation again`;
  private readonly readFailedMessage = $localize`:@@onboarding.enable.read-failed:The activation could not be read. Reading it again is safe.`;
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
  protected readonly projectLabel = $localize`:@@onboarding.enable.project:Project`;
  protected readonly activatedByLabel = $localize`:@@onboarding.enable.activated-by:Activated by`;
  protected readonly previousRevisionLabel = $localize`:@@onboarding.enable.previous-revision:Revision this replaced`;
  protected readonly unavailableLabel = $localize`:@@onboarding.enable.unavailable:Unavailable`;

  constructor() {
    // A scope change abandons the intent and the appended revision: the next confirm belongs to a
    // different project, so its key must not be the old one. `reset()` also invalidates a running
    // attempt (its settlement then writes nothing), which is the guard a data-service call cannot
    // provide on its own.
    effect(() => {
      this.orgId();
      this.projectId();
      this.activationRead.set({ kind: 'loading' });
      this.appendedRevision.set(null);
      this.activation.reset();
      void this.readActivation();
    });
    // QA-01: the confirm disappears on success, so the focus it carried would be dropped. The
    // terminal heading is the announcement target (`tabindex="-1"`, `role="status"` on the panel):
    // focus moves there once the panel exists. Reading the ref inside this effect keeps it tracked,
    // so the pass that mounts the panel re-runs it with a resolved element.
    effect(() => {
      if (this.enabled() === null) {
        return;
      }
      this.enabledHeadingRef()?.nativeElement.focus();
    });
    this.destroyRef.onDestroy(() => {
      this.readGeneration++;
      this.activation.reset();
    });
  }

  /** The confirm action, once per user intent (guarded by the runner while it runs). */
  protected async confirm(): Promise<void> {
    if (this.confirmDisabled()) return;
    await this.activation.run();
  }

  /** An explicit retry of the same intent: same key, same appended revision. */
  protected async retry(): Promise<void> {
    if (this.retryDisabled()) return;
    if (this.readFailure() !== null) {
      await this.readActivation();
      return;
    }
    if (!this.retryEligible()) return;
    await this.activation.retry();
  }

  /** A generation invalidates late answers, including an org change that keeps the project id. */
  protected async readActivation(): Promise<void> {
    const generation = ++this.readGeneration;
    const orgId = this.orgId();
    const projectId = this.projectId();
    this.activationRead.set({ kind: 'loading' });
    const current = (): boolean =>
      !this.destroyRef.destroyed &&
      this.readGeneration === generation &&
      this.orgId() === orgId &&
      this.projectId() === projectId;
    try {
      const state = await this.data.readPolicyActivation(projectId);
      if (current()) {
        this.activationRead.set(state === null ? { kind: 'none' } : { kind: 'active', state });
      }
    } catch (error: unknown) {
      if (!current()) return;
      // A cancelled request is not a failure: the scope moved, or the page went away.
      if (error instanceof DOMException && error.name === 'AbortError') return;
      // A throw that is not the gateway's typed failure is a bug in the call path, not an absence of
      // activation: it still becomes a rendered state with a way back, never a stuck loading panel.
      const failure: ApiError = isApiRequestError(error)
        ? error.error
        : {
            kind: 'unexpected',
            status: 0,
            code: 'client_error',
            message: this.readFailedMessage,
            requestId: null,
            retryable: true,
            details: [],
            retryAfterSeconds: null,
          };
      this.activationRead.set(
        failure.status === 403 ? { kind: 'forbidden' } : { kind: 'error', error: failure },
      );
    }
  }

  /** Advisory has a translated label; every other server mode is rendered verbatim. */
  protected modeText(mode: PolicyActivationStateDto['mode']): string {
    return mode === 'advisory' ? this.advisoryModeLabel : mode;
  }

  /**
   * Append the default body, then activate it — both under `intent`.
   *
   * The scope is captured once so a change mid-flight cannot split the chain across two projects,
   * and F3 closes the window between the two calls: an append that settles after the page went away
   * or the scope moved must NOT be followed by the activation command, which would otherwise commit
   * a `policy_activation` for a view the user has left. The chain is abandoned the way the runner
   * understands (`AbortError`), so no failure is shown and the half-written append is harmless — it
   * is one immutable revision that a later confirm reuses by content hash.
   *
   * The append is skipped when this intent already produced a revision (the retry path); otherwise
   * the constant body is sent unchanged, which is what makes a repeat append idempotent by content.
   */
  private async confirmPolicy(intent: Intent): Promise<PolicyActivationDto> {
    const orgId = this.orgId();
    const projectId = this.projectId();
    const revision = await this.appendDefaultPolicy(projectId, intent);
    if (this.destroyRef.destroyed || this.orgId() !== orgId || this.projectId() !== projectId) {
      throw new DOMException('The step was left before the activation was sent.', 'AbortError');
    }
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
