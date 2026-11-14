// Server-derived connection metadata and a component-scoped, one-time reveal: secret-bearing
// commands never create an Idempotency intent and never auto-retry.
import { DatePipe, DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
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
  focusFieldAfterRender,
  focusRefusalHeading,
  focusWaitingLine,
  requestRefusalFocus,
} from '../../../shared/a11y/step-focus';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { ErrorPatternComponent } from '../../../shared/ui/states';
import { CONNECT_LIST_LIMIT } from '../data/onboarding-progress.service';
import { CliQuickstartComponent } from '../health/cli-quickstart.component';
import { ConnectionHealthComponent } from '../health/connection-health.component';
import { deriveConnectionHealth, type ConnectionHealth } from '../health/connection-health';
import { HealthBoundaryClock } from '../health/health-boundary';
import { OnboardingStore } from '../onboarding.store';
import {
  MAX_REF_ENTRIES,
  MAX_TOKEN_NAME_LENGTH,
  connectionLines,
  invalidRepositoryId,
  invalidRepositoryName,
  invalidTokenName,
  validConnectionLines,
} from './connect-ci.validation';
import { describedBy, fieldDetails, reasonFor } from './field-details';
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
  private readonly injector = inject(Injector);
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
  protected readonly oidcSubmitted = signal(false);
  protected readonly tokenSubmitted = signal(false);
  private readonly draftScope = computed(() => ({
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
  protected readonly serverRepoIdError = computed(() => this.serverReason('repository_id'));
  protected readonly serverRepoNameError = computed(() =>
    this.serverReason('repository_full_name'),
  );
  protected readonly serverRefsError = computed(() => this.serverReason('allowed_ref_patterns'));
  protected readonly serverWorkflowsError = computed(() =>
    this.serverReason('allowed_workflow_refs'),
  );
  protected readonly serverTokenNameError = computed(() => this.serverReason('name'));

  /** Clears one field's displayed reason as soon as the visitor edits it; the alert keeps its rows. */
  protected dismissServerReason(field: string): void {
    this.dismissed.set(dismissFieldReason(this.dismissed(), this.failure(), field));
  }
  protected readonly describedBy = describedBy;

  protected readonly intro = $localize`:@@onboarding.connect.intro:Choose one connection method. Next becomes available only after the server confirms a binding or token.`;
  protected readonly requiredFields = $localize`:@@onboarding.fields.required:Fields marked * are required.`;
  protected readonly repoIdHint = $localize`:@@onboarding.connect.repo-id-hint:Enter the repository's numeric GitHub ID, not its owner/repo name.`;
  protected readonly repoNameHint = $localize`:@@onboarding.connect.repo-name-hint:Use the GitHub owner and repository name, for example acme/web.`;
  protected readonly tokenNameHint = $localize`:@@onboarding.connect.token-name-hint:Choose a name that identifies this token in your CI setup.`;
  protected readonly oidcTitle = $localize`:@@onboarding.connect.oidc-title:GitHub Actions (OIDC)`;
  protected readonly oidcHint = $localize`:@@onboarding.connect.oidc-hint:Set the one-time challenge as the repository variable EVIDGATE_BINDING_CHALLENGE. The binding becomes active after the first CI exchange; the browser never exchanges CI credentials.`;
  protected readonly workflowHint = $localize`:@@onboarding.connect.workflow-hint:In your workflow, grant permissions: id-token: write. Pass the GitHub OIDC JWT and EVIDGATE_BINDING_CHALLENGE to your CI-side Evidgate exchange. Keep the challenge out of workflow files and logs.`;
  protected readonly repoIdLabel = $localize`:@@onboarding.connect.repo-id:GitHub numeric repository ID`;
  protected readonly repoIdError = $localize`:@@onboarding.connect.repo-id-error:Enter a positive whole-number repository ID.`;
  protected readonly repoNameLabel = $localize`:@@onboarding.connect.repo-name:Repository (owner/repo)`;
  protected readonly repoNameError = $localize`:@@onboarding.connect.repo-name-error:Enter the repository as owner/repo.`;
  protected readonly refsLabel = $localize`:@@onboarding.connect.refs:Allowed Git ref patterns`;
  protected readonly refsHint = $localize`:@@onboarding.connect.refs-hint:One per line, for example refs/heads/your-branch. Enter 1–${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: patterns.`;
  protected readonly refsError = $localize`:@@onboarding.connect.refs-error:Enter between 1 and ${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: non-empty ref patterns.`;
  protected readonly workflowsLabel = $localize`:@@onboarding.connect.workflows:Allowed workflow refs`;
  protected readonly workflowsHint = $localize`:@@onboarding.connect.workflows-hint:One per line, for example owner/repo/.github/workflows/ci.yml@refs/heads/your-branch. Enter 1–${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: refs.`;
  protected readonly workflowsError = $localize`:@@onboarding.connect.workflows-error:Enter between 1 and ${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: non-empty workflow refs.`;
  protected readonly bindingSubmit = $localize`:@@onboarding.connect.binding-submit:Create CI binding`;
  protected readonly bindingPermission = $localize`:@@onboarding.connect.binding-permission:Ask your organisation owner for permission to create CI bindings.`;
  protected readonly tokenTitle = $localize`:@@onboarding.connect.token-title:Ingest token`;
  protected readonly tokenHint = $localize`:@@onboarding.connect.token-hint:Use an ingest token when OIDC is not available. Save the one-time value in your CI secret manager, never in source control.`;
  protected readonly tokenNameLabel = $localize`:@@onboarding.connect.token-name:Token name`;
  protected readonly tokenNameError = $localize`:@@onboarding.connect.token-name-error:Enter a token name between 1 and ${MAX_TOKEN_NAME_LENGTH}:MAX_TOKEN_NAME_LENGTH: characters.`;
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
  protected readonly pendingLabel = $localize`:@@onboarding.connect.pending:Pending verification`;
  protected readonly activeLabel = $localize`:@@onboarding.connect.active:Active`;
  protected readonly disabledLabel = $localize`:@@onboarding.connect.disabled:Disabled`;
  protected readonly rotatedLabel = $localize`:@@onboarding.connect.rotated:Rotated`;
  protected readonly expiredLabel = $localize`:@@onboarding.connect.expired:Expired`;
  protected readonly revokedLabel = $localize`:@@onboarding.connect.revoked:Revoked`;
  protected readonly uncertainLabel = $localize`:@@onboarding.connect.response-uncertain:It may have been saved. Reload the connection data before trying again.`;
  protected readonly revealShownLabel = $localize`:@@onboarding.connect.reveal-shown:A one-time value is shown. Save it before leaving this step.`;
  protected readonly truncatedLabel = $localize`:@@onboarding.connect.list-truncated:Showing the first ${CONNECT_LIST_LIMIT}:CONNECT_LIST_LIMIT: entries; more exist, so a newer entry may not appear here.`;

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
    // The refusal heading is the page's own focus target: outside both alerts, so the move neither
    // repeats an announcement nor leaves the visitor on `<body>` after a disabled press. The cue is
    // consumed after render, so a refusal whose heading never renders cannot leave it armed.
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

  protected statusText(status: CiBindingView['status'] | IngestTokenView['status']): string {
    switch (status) {
      case 'pending_verification':
        return this.pendingLabel;
      case 'active':
        return this.activeLabel;
      case 'disabled':
        return this.disabledLabel;
      case 'rotated':
        return this.rotatedLabel;
      case 'expired':
        return this.expiredLabel;
      case 'revoked':
        return this.revokedLabel;
    }
  }

  protected async createBinding(): Promise<void> {
    this.oidcSubmitted.set(true);
    if (this.busy() || !this.bindingPermitted() || !this.active()) return;
    const invalidId = [
      this.repoIdInvalid() && 'connect-oidc-repo-id',
      this.repoNameInvalid() && 'connect-oidc-repo-name',
      this.refsInvalid() && 'connect-oidc-refs',
      this.workflowsInvalid() && 'connect-oidc-workflows',
    ].find((id) => id !== false);
    if (invalidId) {
      this.focusInvalid(invalidId);
      return;
    }
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
        await this.store.refresh(orgId, projectId);
      },
      version,
      orgId,
      projectId,
    );
  }

  protected async createToken(): Promise<void> {
    this.tokenSubmitted.set(true);
    if (this.busy() || !this.tokenPermitted() || !this.active()) return;
    if (this.tokenNameInvalid()) {
      this.focusInvalid('connect-token-name');
      return;
    }
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
        await this.store.refresh(orgId, projectId);
      },
      version,
      orgId,
      projectId,
    );
  }

  private focusInvalid(id: string): void {
    const version = this.scopeVersion;
    const orgId = this.orgId();
    const projectId = this.projectId();
    focusFieldAfterRender({
      injector: this.injector,
      host: this.host,
      id,
      current: () => this.isCurrent(version, orgId, projectId),
    });
  }

  protected async rotate(token: IngestTokenView): Promise<void> {
    if (!this.active() || this.busy() || !this.tokenPermitted() || token.status !== 'active')
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
        await this.store.refresh(orgId, projectId);
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
