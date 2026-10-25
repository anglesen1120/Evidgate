// 09.2.1-T2 (AC2/AC7, §8): server-derived connection metadata and component-scoped,
// one-time reveal. Secret-bearing commands never create an Idempotency intent or auto-retry.
import { DatePipe } from '@angular/common';
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
import { FormsModule } from '@angular/forms';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { RequestStateComponent } from '../../../shared/ui/states';
import { OnboardingStore } from '../onboarding.store';

@Component({
  selector: 'app-onboarding-connect-ci',
  imports: [
    DatePipe,
    FormsModule,
    StatusPillComponent,
    ConnectStepRevealComponent,
    RequestStateComponent,
  ],
  providers: [RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './connect-ci.page.html',
  styleUrls: ['../../../shared/ui/states/state-panel.css', './connect-ci.page.css'],
})
export class ConnectCiPage {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
  protected readonly store = inject(OnboardingStore);
  protected readonly slot = inject(RevealSlot);
  private readonly data = inject(OnboardingDataService);
  private readonly session = inject(SessionStore);
  private readonly destroyRef = inject(DestroyRef);
  private scopeVersion = 0;
  protected readonly busy = signal(false);
  protected readonly refreshing = signal(false);
  protected readonly failure = signal<ApiError | null>(null);
  protected readonly oidcSubmitted = signal(false);
  protected readonly tokenSubmitted = signal(false);
  protected readonly repositoryId = signal('');
  protected readonly repositoryName = signal('');
  protected readonly refs = signal('');
  protected readonly workflows = signal('');
  protected readonly tokenName = signal('');
  // Metadata only: this timestamp is safe to retain after a rotation reveal is dismissed.
  protected readonly overlapEndsAt = signal<string | null>(null);
  protected readonly bindingPermitted = computed(() => this.session.has('ci_binding:write'));
  protected readonly tokenPermitted = computed(() => this.session.has('ingest_token:write'));
  protected readonly repoIdInvalid = computed(
    () =>
      !/^\d+$/.test(this.repositoryId().trim()) ||
      !Number.isSafeInteger(Number(this.repositoryId())) ||
      Number(this.repositoryId()) <= 0,
  );
  protected readonly repoNameInvalid = computed(
    () => !/^[^/\s]+\/[^/\s]+$/.test(this.repositoryName().trim()),
  );
  protected readonly refsInvalid = computed(() => !this.validLines(this.refs()));
  protected readonly workflowsInvalid = computed(() => !this.validLines(this.workflows()));
  protected readonly tokenNameInvalid = computed(
    () => this.tokenName().trim().length < 1 || this.tokenName().trim().length > 128,
  );

  protected readonly intro = $localize`:@@onboarding.connect.intro:Choose one connection method. Next becomes available only after the server confirms a binding or token.`;
  protected readonly oidcTitle = $localize`:@@onboarding.connect.oidc-title:GitHub Actions (OIDC)`;
  protected readonly oidcHint = $localize`:@@onboarding.connect.oidc-hint:Set the one-time challenge as the repository variable EVIDGATE_BINDING_CHALLENGE. The binding becomes active after the first CI exchange; the browser never exchanges CI credentials.`;
  protected readonly workflowHint = $localize`:@@onboarding.connect.workflow-hint:In your workflow, grant permissions: id-token: write. Pass the GitHub OIDC JWT and EVIDGATE_BINDING_CHALLENGE to your CI-side Evidgate exchange. Keep the challenge out of workflow files and logs.`;
  protected readonly repoIdLabel = $localize`:@@onboarding.connect.repo-id:GitHub numeric repository ID`;
  protected readonly repoIdError = $localize`:@@onboarding.connect.repo-id-error:Enter a positive whole-number repository ID.`;
  protected readonly repoNameLabel = $localize`:@@onboarding.connect.repo-name:Repository (owner/repo)`;
  protected readonly repoNameError = $localize`:@@onboarding.connect.repo-name-error:Enter the repository as owner/repo.`;
  protected readonly refsLabel = $localize`:@@onboarding.connect.refs:Allowed Git ref patterns`;
  protected readonly refsHint = $localize`:@@onboarding.connect.refs-hint:One per line, for example refs/heads/your-branch. Enter 1–32 patterns.`;
  protected readonly refsError = $localize`:@@onboarding.connect.refs-error:Enter between 1 and 32 non-empty ref patterns.`;
  protected readonly workflowsLabel = $localize`:@@onboarding.connect.workflows:Allowed workflow refs`;
  protected readonly workflowsHint = $localize`:@@onboarding.connect.workflows-hint:One per line, for example owner/repo/.github/workflows/ci.yml@refs/heads/your-branch. Enter 1–32 refs.`;
  protected readonly workflowsError = $localize`:@@onboarding.connect.workflows-error:Enter between 1 and 32 non-empty workflow refs.`;
  protected readonly bindingSubmit = $localize`:@@onboarding.connect.binding-submit:Create CI binding`;
  protected readonly bindingPermission = $localize`:@@onboarding.connect.binding-permission:Ask your organisation owner for permission to create CI bindings.`;
  protected readonly tokenTitle = $localize`:@@onboarding.connect.token-title:Ingest token`;
  protected readonly tokenHint = $localize`:@@onboarding.connect.token-hint:Use an ingest token when OIDC is not available. Save the one-time value in your CI secret manager, never in source control.`;
  protected readonly tokenNameLabel = $localize`:@@onboarding.connect.token-name:Token name`;
  protected readonly tokenNameError = $localize`:@@onboarding.connect.token-name-error:Enter a token name between 1 and 128 characters.`;
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
  protected readonly refreshLabel = $localize`:@@onboarding.connect.refresh:Retry loading metadata`;
  protected readonly pendingLabel = $localize`:@@onboarding.connect.pending:Pending verification`;
  protected readonly activeLabel = $localize`:@@onboarding.connect.active:Active`;
  protected readonly disabledLabel = $localize`:@@onboarding.connect.disabled:Disabled`;
  protected readonly rotatedLabel = $localize`:@@onboarding.connect.rotated:Rotated`;
  protected readonly expiredLabel = $localize`:@@onboarding.connect.expired:Expired`;
  protected readonly revokedLabel = $localize`:@@onboarding.connect.revoked:Revoked`;

  constructor() {
    effect(() => {
      this.orgId();
      this.projectId();
      this.scopeVersion += 1;
      this.slot.clear();
      this.failure.set(null);
      this.overlapEndsAt.set(null);
    });
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

  private lines(value: string): string[] {
    return value
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean);
  }

  private validLines(value: string): boolean {
    const count = this.lines(value).length;
    return count >= 1 && count <= 32;
  }

  protected async createBinding(): Promise<void> {
    this.oidcSubmitted.set(true);
    if (
      this.busy() ||
      !this.bindingPermitted() ||
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
        const response = await this.data.createCiBinding(orgId, projectId, {
          provider: 'github_actions',
          repository_id: Number(this.repositoryId()),
          repository_full_name: this.repositoryName().trim(),
          allowed_ref_patterns: this.lines(this.refs()),
          allowed_workflow_refs: this.lines(this.workflows()),
        });
        if (!this.isCurrent(version, orgId, projectId)) return;
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
    if (this.busy() || !this.tokenPermitted() || this.tokenNameInvalid()) return;
    const orgId = this.orgId();
    const projectId = this.projectId();
    const version = this.scopeVersion;
    await this.perform(
      async () => {
        const response = await this.data.createIngestToken(projectId, {
          name: this.tokenName().trim(),
        });
        if (!this.isCurrent(version, orgId, projectId)) return;
        this.slot.show({ source: 'ingest-token', value: response.secret });
        await this.store.refresh(orgId, projectId);
      },
      version,
      orgId,
      projectId,
    );
  }

  protected async rotate(token: IngestTokenView): Promise<void> {
    if (this.busy() || !this.tokenPermitted() || token.status !== 'active') return;
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
    if (this.busy() || this.refreshing()) return;
    this.refreshing.set(true);
    try {
      await this.store.refresh(this.orgId(), this.projectId());
    } finally {
      this.refreshing.set(false);
    }
  }

  private isCurrent(version: number, orgId: string, projectId: string): boolean {
    return (
      !this.destroyRef.destroyed &&
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
    this.busy.set(true);
    this.failure.set(null);
    this.slot.clear();
    this.overlapEndsAt.set(null);
    try {
      await action();
    } catch (error: unknown) {
      if (!isApiRequestError(error)) throw error;
      if (this.isCurrent(version, orgId, projectId)) this.failure.set(error.error);
    } finally {
      this.busy.set(false);
    }
  }
}
