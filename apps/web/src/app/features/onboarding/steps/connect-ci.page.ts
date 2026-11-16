// Server-derived connection metadata and a component-scoped, one-time reveal: secret-bearing
// commands never create an Idempotency intent and never auto-retry.
import { DatePipe, DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  LOCALE_ID,
  afterRenderEffect,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import {
  RefreshFocusSession,
  releaseRefreshFocus,
  restoreRefreshFocus,
} from '../../../shared/a11y/refresh-focus';
import {
  focusRefusalHeading,
  focusWaitingLine,
  requestRefusalFocus,
} from '../../../shared/a11y/step-focus';
import { shortDateFormat } from '../../../shared/time/short-date-format';
import {
  FieldFeedbackComponent,
  FormErrorSummaryComponent,
  FormRequiredNoteComponent,
} from '../../../shared/forms';
import {
  DesktopMutationDirective,
  DesktopRequiredNoticeComponent,
  NarrowMutationPolicy,
} from '../../../shared/ui/narrow';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { ErrorPatternComponent, InlineFeedbackComponent } from '../../../shared/ui/states';
import { CONNECT_LIST_LIMIT } from '../data/onboarding-progress.service';
import { CliQuickstartComponent } from '../health/cli-quickstart.component';
import { ConnectionHealthComponent } from '../health/connection-health.component';
import { deriveConnectionHealth, type ConnectionHealth } from '../health/connection-health';
import { CiRotateConfirmComponent, type RotateScope } from './connect-ci-rotate-confirm.component';
import { HealthBoundaryClock } from '../health/health-boundary';
import { OnboardingStore } from '../onboarding.store';
import { createBindingFeedback, createTokenFeedback } from './connect-ci.form-feedback';
import {
  connectionLines,
  invalidRepositoryId,
  invalidRepositoryName,
  invalidTokenName,
  validConnectionLines,
} from './connect-ci.validation';
import { fieldDetails, reasonFor } from './field-details';
import {
  NO_DISMISSED_REASONS,
  dismissFieldReason,
  showsFieldReason,
  type DismissedReasons,
} from './server-reasons';

@Component({
  selector: 'app-onboarding-connect-ci',
  imports: [
    DatePipe,
    FormsModule,
    StatusPillComponent,
    ConnectStepRevealComponent,
    ConnectionHealthComponent,
    CliQuickstartComponent,
    ErrorPatternComponent,
    InlineFeedbackComponent,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
    CiRotateConfirmComponent,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmInput,
    HlmLabel,
  ],
  providers: [RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './connect-ci.page.html',
  host: { class: 'block min-w-0' },
})
export class ConnectCiPage {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
  readonly active = input(true);
  protected readonly store = inject(OnboardingStore);
  protected readonly slot = inject(RevealSlot);
  private readonly data = inject(OnboardingDataService);
  private readonly session = inject(SessionStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly policy = inject(NarrowMutationPolicy);
  protected readonly dateTimeFormat = shortDateFormat(inject(LOCALE_ID), true, 'medium');
  private scopeVersion = 0;
  private readonly healthClock = new HealthBoundaryClock(() => this.store.tokens());
  /** The place a re-read may take, held from the press until the pass that spends it. */
  private readonly refreshFocus = new RefreshFocusSession(this.document);
  private readonly bindingHeading = viewChild<ElementRef<HTMLHeadingElement>>('bindingHeading');
  private readonly tokenHeading = viewChild<ElementRef<HTMLHeadingElement>>('tokenHeading');
  private readonly failureHeadingRef = viewChild<ElementRef<HTMLElement>>('failureHeadingEl');
  private readonly busyStatusRef = viewChild<ElementRef<HTMLElement>>('busyStatus');
  /** Only this page's own refused command requests a focus handoff. */
  private readonly announceFailure = signal(false);
  protected readonly busy = signal(false);
  protected readonly refreshing = signal(false);
  protected readonly failure = signal<ApiError | null>(null);
  // These one-time commands are never safe to replay, even if the transport error is retryable.
  protected readonly displayFailure = computed(() => {
    const failure = this.failure();
    return failure ? { ...failure, retryable: false } : null;
  });
  protected readonly responseUncertain = computed(
    () => this.failure()?.kind === 'timeout' || this.failure()?.kind === 'network',
  );
  protected readonly draftScope = computed(() => ({
    orgId: this.orgId(),
    projectId: this.projectId(),
  }));
  protected readonly repositoryId = linkedSignal({
    source: this.draftScope,
    computation: () => '',
  });
  protected readonly repositoryName = linkedSignal({
    source: this.draftScope,
    computation: () => '',
  });
  protected readonly refs = linkedSignal({ source: this.draftScope, computation: () => '' });
  protected readonly workflows = linkedSignal({ source: this.draftScope, computation: () => '' });
  protected readonly tokenName = linkedSignal({ source: this.draftScope, computation: () => '' });
  // Metadata only: this timestamp is safe to retain after a rotation reveal is dismissed.
  protected readonly overlapEndsAt = signal<string | null>(null);
  protected readonly bindingPermitted = computed(() => this.session.has('ci_binding:write'));
  protected readonly tokenPermitted = computed(() => this.session.has('ingest_token:write'));
  protected readonly repoIdInvalid = computed(() => invalidRepositoryId(this.repositoryId()));
  protected readonly repoNameInvalid = computed(() => invalidRepositoryName(this.repositoryName()));
  protected readonly refsInvalid = computed(() => !validConnectionLines(this.refs()));
  protected readonly workflowsInvalid = computed(() => !validConnectionLines(this.workflows()));
  protected readonly tokenNameInvalid = computed(() => invalidTokenName(this.tokenName()));
  protected readonly details = computed(() =>
    fieldDetails(this.failure()?.details ?? [], 'connection'),
  );
  /** The reasons the visitor has answered by editing a field; see `server-reasons.ts`. */
  private readonly dismissed = signal<DismissedReasons>(NO_DISMISSED_REASONS);
  /** Editing a field dismisses its submitted reason, while the alert retains the diagnostics. */
  private serverReason(field: string): string | null {
    if (!showsFieldReason(this.dismissed(), this.failure(), field)) return null;
    return reasonFor(this.details(), field);
  }

  /** Clears one field's displayed reason as soon as the visitor edits it; the alert keeps its rows. */
  protected dismissServerReason(field: string): void {
    this.dismissed.set(dismissFieldReason(this.dismissed(), this.failure(), field));
  }

  protected readonly intro = $localize`:@@onboarding.connect.intro:Choose one connection method. Next becomes available only after the server confirms a binding or token.`;
  protected readonly oidcTitle = $localize`:@@onboarding.connect.oidc-title:GitHub Actions (OIDC)`;
  protected readonly oidcHint = $localize`:@@onboarding.connect.oidc-hint:Set the one-time challenge as the repository variable EVIDGATE_BINDING_CHALLENGE. The binding becomes active after the first CI exchange; the browser never exchanges CI credentials.`;
  protected readonly workflowHint = $localize`:@@onboarding.connect.workflow-hint:In your workflow, grant permissions: id-token: write. Pass the GitHub OIDC JWT and EVIDGATE_BINDING_CHALLENGE to your CI-side Evidgate exchange. Keep the challenge out of workflow files and logs.`;
  protected readonly bindingSubmit = $localize`:@@onboarding.connect.binding-submit:Create CI binding`;
  protected readonly bindingPermission = $localize`:@@onboarding.connect.binding-permission:Ask your organisation owner for permission to create CI bindings.`;
  protected readonly tokenTitle = $localize`:@@onboarding.connect.token-title:Ingest token`;
  protected readonly tokenHint = $localize`:@@onboarding.connect.token-hint:Use an ingest token when OIDC is not available. Save the one-time value in your CI secret manager, never in source control.`;
  protected readonly tokenSubmit = $localize`:@@onboarding.connect.token-submit:Create ingest token`;
  protected readonly tokenPermission = $localize`:@@onboarding.connect.token-permission:Ask your organisation owner for permission to create or rotate ingest tokens.`;
  protected readonly busyLabel = $localize`:@@onboarding.connect.busy:Saving connection…`;
  protected readonly bindingsLabel = $localize`:@@onboarding.connect.bindings:CI bindings`;
  protected readonly tokensLabel = $localize`:@@onboarding.connect.tokens:Ingest tokens`;
  protected readonly emptyBindings = $localize`:@@onboarding.connect.empty-bindings:No CI bindings yet.`;
  protected readonly emptyTokens = $localize`:@@onboarding.connect.empty-tokens:No ingest tokens yet.`;
  protected readonly statusLabel = $localize`:@@onboarding.connect.status:Status`;
  protected readonly createdLabel = $localize`:@@onboarding.connect.created:Created (UTC)`;
  protected readonly prefixLabel = $localize`:@@onboarding.connect.prefix:Prefix`;
  protected readonly scopesLabel = $localize`:@@onboarding.connect.scopes:Scopes`;
  protected readonly expiresLabel = $localize`:@@onboarding.connect.expires:Expires (UTC)`;
  protected readonly usedLabel = $localize`:@@onboarding.connect.last-used:Last used (UTC)`;
  protected readonly noneLabel = $localize`:@@onboarding.connect.not-set:Not set`;
  protected readonly rotateLabel = $localize`:@@onboarding.connect.rotate:Rotate token`;
  protected readonly overlapLabel = $localize`:@@onboarding.connect.overlap:The previous token continues verifying until (UTC):`;
  protected readonly bindingRevealHeading = $localize`:@@onboarding.connect.challenge-heading:Save your binding challenge`;
  protected readonly tokenRevealHeading = $localize`:@@onboarding.connect.token-heading:Save your ingest token`;
  protected readonly revealWarning = $localize`:@@onboarding.connect.reveal-warning:Shown only once. Save this value before leaving this step. If copying fails, select and copy the value manually.`;
  protected readonly copyLabel = $localize`:@@onboarding.connect.copy:Copy value`;
  protected readonly copiedLabel = $localize`:@@onboarding.connect.copied:Value copied.`;
  protected readonly dismissLabel = $localize`:@@onboarding.connect.dismiss:Dismiss secret`;
  protected readonly refreshFailedLabel = $localize`:@@onboarding.connect.refresh-failed:The connection was saved, but progress could not be refreshed. Retry loading metadata; do not create the connection again.`;
  protected readonly failureHeading = $localize`:@@onboarding.connect.failure-heading:The connection was not saved`;
  protected readonly detailsLabel = $localize`:@@onboarding.connect.details-label:The server refused these values`;
  protected readonly refreshLabel = $localize`:@@onboarding.connect.refresh:Retry loading metadata`;
  protected readonly pendingLabel = $localize`:@@onboarding.connect.awaiting-ci:Awaiting CI`;
  protected readonly activeLabel = $localize`:@@onboarding.connect.active:Active`;
  protected readonly disabledLabel = $localize`:@@onboarding.connect.disabled:Disabled`;
  protected readonly rotatedLabel = $localize`:@@onboarding.connect.rotated:Rotated`;
  protected readonly expiredLabel = $localize`:@@onboarding.connect.expired:Expired`;
  protected readonly revokedLabel = $localize`:@@onboarding.connect.revoked:Revoked`;
  protected readonly uncertainLabel = $localize`:@@onboarding.connect.response-uncertain:It may have been saved. Reload the connection data before trying again.`;
  protected readonly revealShownLabel = $localize`:@@onboarding.connect.reveal-shown:A one-time value is shown. Save it before leaving this step.`;
  protected readonly truncatedLabel = $localize`:@@onboarding.connect.list-truncated:Showing the first ${CONNECT_LIST_LIMIT}:CONNECT_LIST_LIMIT: entries; more exist, so a newer entry may not appear here.`;
  protected readonly subsectionHeadingSize = 'subsection' as const;
  protected readonly bindingVariant = computed(() =>
    this.store.bindings().length + this.store.tokens().length > 0 ? 'outline' : 'default',
  );
  protected readonly bindingFeedback = createBindingFeedback(
    {
      repositoryId: this.repositoryId,
      repositoryName: this.repositoryName,
      refs: this.refs,
      workflows: this.workflows,
    },
    (field) => this.serverReason(field),
  );
  protected readonly tokenFeedback = createTokenFeedback(this.tokenName, (field) =>
    this.serverReason(field),
  );
  protected readonly repoIdField = this.bindingFeedback.fields[0]!;
  protected readonly repoNameField = this.bindingFeedback.fields[1]!;
  protected readonly refsField = this.bindingFeedback.fields[2]!;
  protected readonly workflowsField = this.bindingFeedback.fields[3]!;
  protected readonly tokenNameField = this.tokenFeedback.fields[0]!;
  protected readonly statusLabels: Record<
    CiBindingView['status'] | IngestTokenView['status'],
    string
  > = {
    pending_verification: this.pendingLabel,
    active: this.activeLabel,
    disabled: this.disabledLabel,
    rotated: this.rotatedLabel,
    expired: this.expiredLabel,
    revoked: this.revokedLabel,
  };

  /** Uses only metadata already read for this step, never one-time values. */
  protected readonly health = computed<ConnectionHealth>(() =>
    deriveConnectionHealth({
      bindings: this.store.bindings(),
      tokens: this.store.tokens(),
      bindingsTruncated: this.store.bindingsTruncated(),
      tokensTruncated: this.store.tokensTruncated(),
      now: this.healthClock.now(),
    }),
  );
  /** Prefer usable active metadata, then an unexpired rotating token; never retain the secret. */
  protected readonly quickstartToken = computed<{ name: string; prefix: string } | null>(() => {
    const credentials = this.health().credentials;
    const preferred =
      credentials.find(
        (credential) => credential.kind === 'token' && credential.detail === 'active',
      ) ??
      credentials.find(
        (credential) => credential.kind === 'token' && credential.detail === 'rotating',
      );
    const token = preferred
      ? this.store.tokens().find((candidate) => candidate.id === preferred.id)
      : undefined;
    return token ? { name: token.name, prefix: token.prefix } : null;
  });
  /** The repository the snippet references: the first active binding, else the first one. */
  protected readonly quickstartBinding = computed<{ repository_full_name: string } | null>(() => {
    const bindings = this.store.bindings();
    const binding = bindings.find((candidate) => candidate.status === 'active') ?? bindings[0];
    return binding ? { repository_full_name: binding.repository_full_name } : null;
  });
  /** The project slug the loaded progress carries; the snippet keeps its placeholder without it. */
  protected readonly quickstartProjectRef = computed(() => this.store.project()?.slug ?? null);

  constructor() {
    effect(() => {
      this.orgId();
      this.projectId();
      this.bindingFeedback.reset();
      this.tokenFeedback.reset();
    });
    effect(() => {
      this.orgId();
      this.projectId();
      this.active();
      this.scopeVersion += 1;
      this.busy.set(false);
      this.slot.clear();
      this.failure.set(null);
      this.overlapEndsAt.set(null);
    });
    effect((onCleanup) => {
      this.store.tokens();
      if (this.active()) this.healthClock.arm();
      else this.healthClock.stop();
      onCleanup(() => this.healthClock.stop());
    });
    // A retained step must discard one-time values when its session ends.
    const endOfSession = this.session.onSessionEnd(() => {
      this.scopeVersion += 1;
      this.healthClock.stop();
      this.slot.clear();
    });
    // Refusal focus is consumed after render so a missing heading cannot leave it armed.
    focusRefusalHeading({
      request: this.announceFailure,
      heading: this.failureHeadingRef,
      afterRender: true,
    });
    // A re-read may remove what the visitor pressed, so the place is put back after the DOM commits.
    afterRenderEffect({
      write: () => {
        const handoff = this.refreshFocus.spend();
        if (handoff === null) return;
        releaseRefreshFocus(this.document, handoff);
        restoreRefreshFocus(this.document, handoff, this.scopeVersion, {
          binding: this.bindingHeading()?.nativeElement,
          token: this.tokenHeading()?.nativeElement,
        });
      },
    });
    this.destroyRef.onDestroy(() => {
      this.healthClock.stop();
      // The pass that spends a held place never runs on a destroyed page, so release it here.
      this.refreshFocus.release();
      endOfSession();
    });
  }

  protected dismissReveal(): void {
    const heading =
      this.slot.current()?.source === 'binding-challenge'
        ? this.bindingHeading()
        : this.tokenHeading();
    this.slot.clear();
    heading?.nativeElement.focus();
  }

  /** The accessible name of one row's Rotate button: the visible label repeats per row. */
  protected rotateNamed(name: string): string {
    return $localize`:@@onboarding.connect.rotate-named:Rotate token ${name}:TOKEN_NAME:`;
  }

  protected async createBinding(): Promise<void> {
    if (!this.policy.allowsMutation() || this.busy() || !this.bindingPermitted() || !this.active())
      return;
    this.bindingFeedback.submit();
    if (
      this.repoIdInvalid() ||
      this.repoNameInvalid() ||
      this.refsInvalid() ||
      this.workflowsInvalid()
    )
      return;
    const orgId = this.orgId();
    const projectId = this.projectId();
    const version = this.scopeVersion;
    await this.perform(
      async () => {
        const response = await this.data.createCiBinding(projectId, {
          provider: 'github_actions',
          repository_id: Number(this.repositoryId()),
          repository_full_name: this.repositoryName().trim(),
          allowed_ref_patterns: connectionLines(this.refs()),
          allowed_workflow_refs: connectionLines(this.workflows()),
        });
        if (!this.isCurrent(version, orgId, projectId)) return;
        this.overlapEndsAt.set(null);
        this.slot.show({ source: 'binding-challenge', value: response.binding_challenge });
      },
      version,
      orgId,
      projectId,
    );
  }

  protected async createToken(): Promise<void> {
    if (!this.policy.allowsMutation() || this.busy() || !this.tokenPermitted() || !this.active())
      return;
    this.tokenFeedback.submit();
    if (this.tokenNameInvalid()) return;
    const orgId = this.orgId();
    const projectId = this.projectId();
    const version = this.scopeVersion;
    await this.perform(
      async () => {
        const response = await this.data.createIngestToken(projectId, {
          name: this.tokenName().trim(),
        });
        if (!this.isCurrent(version, orgId, projectId)) return;
        this.overlapEndsAt.set(null);
        this.slot.show({ source: 'ingest-token', value: response.secret });
      },
      version,
      orgId,
      projectId,
    );
  }

  protected async rotate(token: IngestTokenView, scope: RotateScope): Promise<void> {
    if (
      scope !== this.draftScope() ||
      !this.policy.allowsMutation() ||
      !this.active() ||
      this.busy() ||
      !this.tokenPermitted() ||
      !this.store.tokens().some((current) => current.id === token.id && current.status === 'active')
    )
      return;
    const orgId = this.orgId();
    const projectId = this.projectId();
    const version = this.scopeVersion;
    await this.perform(
      async () => {
        const response = await this.data.rotateIngestToken(token.id);
        if (!this.isCurrent(version, orgId, projectId)) return;
        this.slot.show({ source: 'ingest-token-rotation', value: response.secret });
        this.overlapEndsAt.set(response.overlap_ends_at ?? null);
      },
      version,
      orgId,
      projectId,
    );
  }

  protected async refresh(): Promise<void> {
    if (this.destroyRef.destroyed || this.busy() || this.refreshing()) return;
    // Where the visitor is, captured before the read can replace what they pressed. Whether that
    // place survives is only knowable once the DOM has committed, so the move is decided there.
    this.refreshFocus.capture(this.host.nativeElement, this.scopeVersion, (control) =>
      control.closest('[data-testid="connect-token-panel"]') === null ? 'binding' : 'token',
    );
    this.refreshing.set(true);
    try {
      await this.store.refresh(this.orgId(), this.projectId());
    } finally {
      // A destroyed page owes no move: onDestroy already released the place this read held.
      if (!this.destroyRef.destroyed) {
        this.refreshing.set(false);
        this.refreshFocus.settle();
      }
    }
  }

  private isCurrent(version: number, orgId: string, projectId: string): boolean {
    return (
      !this.destroyRef.destroyed &&
      this.active() &&
      version === this.scopeVersion &&
      this.orgId() === orgId &&
      this.projectId() === projectId
    );
  }

  private async perform(
    action: () => Promise<void>,
    version: number,
    orgId: string,
    projectId: string,
  ): Promise<void> {
    // Every command disables the control it was pressed from, so the waiting line takes the place
    // before that happens and holds it until the answer hands it on (a reveal, or a refusal).
    focusWaitingLine(this.busyStatusRef);
    this.busy.set(true);
    this.failure.set(null);
    try {
      await action();
      // A stale answer still means the server committed: the one-time value stays dropped, and the
      // metadata of the scope the command was issued in is reconciled instead of going missing.
      if (!this.destroyRef.destroyed && this.orgId() === orgId && this.projectId() === projectId)
        await this.store.refresh(orgId, projectId);
    } catch (error: unknown) {
      if (!isApiRequestError(error)) throw error;
      if (this.isCurrent(version, orgId, projectId)) {
        this.failure.set(error.error);
        requestRefusalFocus(this.announceFailure, this.failure());
        if (error.error.kind === 'timeout' || error.error.kind === 'network') {
          // Reconcile metadata only: the failed response may hide a committed write.
          await this.store.refresh(orgId, projectId);
        }
      }
    } finally {
      if (this.isCurrent(version, orgId, projectId)) this.busy.set(false);
    }
  }
}
