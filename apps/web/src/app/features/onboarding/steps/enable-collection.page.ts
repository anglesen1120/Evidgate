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
// Terminal metadata is interpolated, ids and the mode are sanitised, and the hash is sanitised
// before shortening.
// The heading takes focus after either a successful read or confirm. Refusals use app-error-pattern
// so the typed server message, code and request id are preserved rather than replaced by fixed copy.
//
// F63 completes the read's own failure handling: a failed read keeps the answer's `Retry-After`
// window (the runner's rule for the confirm, measured on the page's injected clock, with the
// countdown as text rather than a live region), both read controls stay disabled until it elapses,
// a re-read the user asked for returns focus to the named failure heading or to the 403 recovery
// control, and a refused read offers exactly one control that re-runs the read and never a command.

import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import type {
  PolicyActivationDto,
  PolicyActivationStateDto,
  PolicyRevisionDto,
} from '@evidgate/sdk';

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
import { fieldDetails } from './field-details';

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

/** Largest delay a browser or Node timer accepts; a longer interval is clamped to it. */
const MAX_WAIT_DELAY_MS = 2 ** 31 - 1;

/**
 * The clock a failed read's `Retry-After` wait is measured against. `createIntentRunner` takes its
 * clock the same way — an option (`IntentRunnerOptions.now` in `core/http/retry.ts`), never a bare
 * `Date.now` — but a read has no intent and no runner, so the page owns the same rule and the same
 * seam: a spec overrides this provider and advances the wait without waiting for it.
 */
export const READ_CLOCK = new InjectionToken<() => number>('evidgate.onboarding.read-clock', {
  providedIn: 'root',
  factory: () => (): number => Date.now(),
});

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
  /** The clock the confirm's and the read's `Retry-After` windows are measured against. */
  private readonly now = inject(READ_CLOCK);
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
  protected readonly activation = createIntentRunner<PolicyActivationDto>(
    (intent) => this.confirmPolicy(intent),
    { now: () => this.now() },
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
  /**
   * AC-F63-a: a failed read's `Retry-After` window, as this page renders it. The runner owns the
   * same rule for the confirm, but a read has no intent to protect, so the page keeps its own: one
   * deadline from the answer's own `retryAfterSeconds`, one second-tick for the visible countdown
   * (never a poll for eligibility — the tick only re-renders the remainder — and never a live
   * region: the state sheet asks for the interval as text, not a per-second announcement).
   */
  private readonly readWaitDeadline = signal<number | null>(null);
  /** The server's own interval for the open window, in whole seconds; `null` when none is open. */
  private readonly readWaitSupplied = signal<number | null>(null);
  /** The clock reading the countdown is rendered against; rewritten by the tick. */
  private readonly readWaitNow = signal(0);
  private readWaitTimer: ReturnType<typeof setInterval> | null = null;
  /** Whole seconds left in the open window; `null` when no window is open. */
  protected readonly readWaitSeconds = computed<number | null>(() => {
    const deadline = this.readWaitDeadline();
    if (deadline === null) return null;
    const left = deadline - this.readWaitNow();
    return left > 0 ? Math.max(1, Math.ceil(left / 1000)) : null;
  });
  /** True while the failed read's window is open: both read controls stay disabled. */
  protected readonly readWaitOpen = computed(() => this.readWaitSeconds() !== null);
  /**
   * The wait as the panel states it: neutral about *why* the server closed the read (the page shows
   * it for any answer that names an interval, not only a `429`), the time left, and the interval the
   * server itself named — so a reader can tell a countdown from a guess about a missing interval.
   */
  protected readonly readWaitText = computed<string | null>(() => {
    const remaining = this.readWaitSeconds();
    if (remaining === null) return null;
    const supplied = this.readWaitSupplied() ?? remaining;
    return $localize`:@@onboarding.enable.read-wait:The server asked you to wait before reading the activation again: ${remaining}:remaining: s left of the ${supplied}:supplied: s it named.`;
  });
  /**
   * AC-F63-b: the generation of the re-read this page's own press started, so only *that* read's
   * answer may hand focus to the named failure target. The first read, a scope change and the
   * shell's reload leave it `null`, which is what keeps the page from moving focus on a render
   * nobody asked for — and a press whose read was lost to a scope change cannot refocus the answer
   * of a different read.
   */
  private refocusGeneration: number | null = null;
  /**
   * Set when this page's own confirm or retry was refused. The render pass spends it on the
   * refusal's heading, or drops it when no heading can render.
   */
  private readonly announceConfirmFailure = signal(false);
  private readonly readFailureHeadingRef = viewChild<ElementRef<HTMLElement>>('readFailureHeading');
  private readonly confirmFailureHeadingRef =
    viewChild<ElementRef<HTMLElement>>('confirmFailureHeading');
  private readonly busyStatusRef = viewChild<ElementRef<HTMLElement>>('busyStatus');
  private readonly readAgainRef = viewChild<ElementRef<HTMLButtonElement>>('readAgainControl');
  /**
   * Both page-owned failure headings sit above the shared pattern's title, which is therefore one
   * level below them in every branch that renders a failure.
   */
  protected readonly failureHeadingLevel = computed<2 | 3 | 4>(() =>
    this.failureView() !== null ? 4 : 3,
  );
  /** The section's accessible name is the heading of the failure it actually shows. */
  protected readonly failureHeadingId = computed(() =>
    this.readFailure() !== null ? 'enable-read-failed-heading' : 'enable-confirm-failed-heading',
  );
  /** Only a failure the contract calls retryable offers the same-intent retry action. */
  protected readonly retryEligible = computed(
    () =>
      this.permitted() && this.activationRead().kind === 'none' && this.activation.retryEligible(),
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
  /** The failure the panel renders: a failed read first, then a refused confirm. */
  protected readonly failureView = computed(() => this.readFailure() ?? this.failure());
  /** The envelope's field rows of whichever failure is on screen (00 §9.2). */
  protected readonly details = computed(() => fieldDetails(this.failureView()?.details ?? []));
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
   * a failed command retries the same intent only when the contract calls it retryable. A read's
   * `Retry-After` window closes both read controls, because either one would be the same request.
   */
  protected readonly retryDisabled = computed(() =>
    this.readFailure() !== null
      ? this.busy() || this.readWaitOpen()
      : !this.retryEligible() || this.busy(),
  );

  /**
   * A read is side-effect free, so reading it again is always safe — but the shared pattern renders
   * its Retry only for a failure the contract calls retryable, and a 401 or a 5xx is not one. Rather
   * than claim a retryable refusal, the page offers its own re-read action for exactly those.
   */
  protected readonly readRetryWithoutAction = computed(() => {
    const failure = this.readFailure();
    return failure !== null && !failure.retryable;
  });
  protected readonly enabledProject = computed(() =>
    sanitizedIdentifier(this.enabled()?.project_id),
  );
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
  /** AC-F63-b: the named target a failed re-read returns focus to. */
  protected readonly readFailedHeading = $localize`:@@onboarding.enable.read-failed-heading:The activation could not be read`;
  protected readonly confirmFailedHeading = $localize`:@@onboarding.enable.confirm-failed-heading:Collection could not be enabled`;
  protected readonly detailsLabel = $localize`:@@onboarding.enable.details-label:The server refused these values`;
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
    this.abandonOnScopeChange();
    this.focusEnabledHeading();
    this.focusReadAnswer();
    this.focusConfirmFailure();
    this.destroyRef.onDestroy(() => {
      this.readGeneration++;
      this.clearReadWait();
      this.activation.reset();
    });
  }

  /**
   * A scope change abandons the intent and its cached revision: the next confirm belongs to another
   * project. `reset()` also stops the running attempt from writing anything when it settles, which
   * the data service cannot do for the page.
   */
  private abandonOnScopeChange(): void {
    effect(() => {
      this.orgId();
      this.projectId();
      this.activationRead.set({ kind: 'loading' });
      this.appendedRevision.set(null);
      this.activation.reset();
      void this.readActivation();
    });
  }

  /**
   * The confirm vanishes on success, so the focus it carried would be dropped; the terminal panel's
   * heading is the announcement target. Reading the ref inside the effect keeps it tracked, so the
   * pass that mounts the panel re-runs it with a resolved element.
   */
  private focusEnabledHeading(): void {
    effect(() => {
      if (this.enabled() === null) {
        return;
      }
      this.enabledHeadingRef()?.nativeElement.focus();
    });
  }

  /**
   * A re-read the user pressed replaces the control it was pressed from, so its answer hands focus
   * to a named target. Only the read this page's own press started qualifies; reading the refs keeps
   * the pass that mounts the target tracked.
   */
  private focusReadAnswer(): void {
    effect(() => {
      const read = this.activationRead();
      // Only the read this page's own press started may move focus, and only while it is still the
      // current one: a scope change bumps `readGeneration`, so a lost press stops qualifying here.
      if (this.refocusGeneration === null || this.refocusGeneration !== this.readGeneration) return;
      if (read.kind === 'loading') return;
      if (read.kind === 'active' || read.kind === 'none') {
        this.refocusGeneration = null;
        return;
      }
      const target = read.kind === 'error' ? this.readFailureHeadingRef() : this.readAgainRef();
      // A target that has not mounted yet leaves the request open: reading the ref keeps it tracked,
      // so the pass that mounts it re-runs this with a resolved element.
      if (target === undefined) return;
      this.refocusGeneration = null;
      target.nativeElement.focus();
    });
  }

  /**
   * The press disabled the confirm, so a refusal hands the place to the page's own heading — never
   * to the alert, whose body a focus move would read a second time. A request whose failure has
   * been cleared, or whose panel a failed read now owns, is dropped rather than spent later.
   */
  private focusConfirmFailure(): void {
    effect(() => {
      if (!this.announceConfirmFailure()) return;
      // The heading renders only for a refusal this page still shows; anything else strands the
      // request, and a stranded one would move focus on a render nobody pressed for.
      if (this.failure() === null || this.readFailure() !== null) {
        this.announceConfirmFailure.set(false);
        return;
      }
      const heading = this.confirmFailureHeadingRef()?.nativeElement;
      if (heading === undefined) return;
      this.announceConfirmFailure.set(false);
      heading.focus();
    });
  }

  /** The confirm action, once per user intent (guarded by the runner while it runs). */
  protected async confirm(): Promise<void> {
    if (this.confirmDisabled()) return;
    this.focusBusy();
    await this.activation.run();
    this.announceFailureIfRefused();
  }

  /** An explicit retry of the same intent: same key, same appended revision. */
  protected async retry(): Promise<void> {
    if (this.retryDisabled()) return;
    if (this.readFailure() !== null) {
      await this.readActivation(true);
      return;
    }
    if (!this.retryEligible()) return;
    this.focusBusy();
    await this.activation.retry();
    this.announceFailureIfRefused();
  }

  /**
   * A refused confirm disables the control it was pressed from, so the page hands focus to its own
   * heading once that renders — the busy line holds the place until then.
   */
  private announceFailureIfRefused(): void {
    if (this.failure() === null) return;
    this.announceConfirmFailure.set(true);
  }

  /** Hands the place to the waiting line, which stays mounted while the chain runs. */
  private focusBusy(): void {
    this.busyStatusRef()?.nativeElement.focus();
  }

  /**
   * AC-F63-c: the only way out of a refused (`403`) read. Reading is side-effect free, so repeating
   * it is always safe — and it is all this control does: the confirm stays natively disabled, so no
   * create or activation can start from here.
   */
  protected async readAgain(): Promise<void> {
    if (this.reading()) return;
    await this.readActivation(true);
  }

  /**
   * Start the failed read's `Retry-After` window from the answer's own interval. The same rule the
   * runner applies to the confirm (`core/http/retry.ts`): an absent, negative or non-finite interval
   * is no interval, so the control opens at once, and a positive one is one timer, never a poll.
   */
  private startReadWait(seconds: number | null): void {
    this.clearReadWait();
    const interval = seconds !== null && Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
    if (interval === null || interval === 0) return;
    const startedAt = this.now();
    this.readWaitSupplied.set(interval);
    this.readWaitNow.set(startedAt);
    // Clamped like a runner's delay: a value above the timer maximum would fire at once and turn a
    // hostile interval into no wait at all.
    this.readWaitDeadline.set(startedAt + Math.min(interval * 1000, MAX_WAIT_DELAY_MS));
    this.readWaitTimer = setInterval(() => {
      const instant = this.now();
      this.readWaitNow.set(instant);
      const deadline = this.readWaitDeadline();
      if (deadline !== null && deadline <= instant) this.clearReadWait();
    }, 1000);
  }

  /** Close the window (the countdown's own deadline, a new attempt, or this page going away). */
  private clearReadWait(): void {
    if (this.readWaitTimer !== null) {
      clearInterval(this.readWaitTimer);
      this.readWaitTimer = null;
    }
    this.readWaitDeadline.set(null);
    this.readWaitSupplied.set(null);
  }

  /**
   * A generation invalidates late answers, including an org change that keeps the project id.
   *
   * `refocus` is set only by this page's own re-read controls: it arms the answer of *this* read to
   * take focus, and every other read — the first one, a scope change, the shell's reload — clears
   * the arm, so a press can never make a later render focus anything.
   */
  protected async readActivation(refocus = false): Promise<void> {
    const generation = ++this.readGeneration;
    this.refocusGeneration = refocus ? generation : null;
    const orgId = this.orgId();
    const projectId = this.projectId();
    // A new attempt abandons the previous failure's window, as `attempt()` does for the runner.
    this.clearReadWait();
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
      // A scope change or a destroyed page already invalidated this generation, abort or not, so
      // nothing is written for it. Everything else — including a cancellation that reached a read
      // this page still owns, whatever its class or realm — is an absent answer, not an absence of
      // activation: it becomes a rendered state with a way back, never a stuck loading panel.
      if (!current()) return;
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
      // AC-F63-a: the answer's own interval opens the read's window; an absent one opens it not at
      // all, so the control is usable at once (the runner's own rule for the confirm). A `403` is a
      // refusal, not a throttled attempt: it offers the read-only recovery instead, so nothing this
      // answer carries may close that control for an interval nobody asked the user to wait.
      if (failure.status !== 403) this.startReadWait(failure.retryAfterSeconds);
      this.activationRead.set(
        failure.status === 403 ? { kind: 'forbidden' } : { kind: 'error', error: failure },
      );
    }
  }

  /**
   * Advisory has a translated label; every other server mode is rendered verbatim — as a sanitised
   * wire value, because the enum is closed but the answer is still server text, and an invisible
   * character in it could reorder the row that identifies the mode.
   */
  protected modeText(mode: PolicyActivationStateDto['mode']): string {
    return mode === 'advisory' ? this.advisoryModeLabel : sanitizeLabel(mode);
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
