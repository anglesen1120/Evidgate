import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmAlertImports } from '@evidgate/ui/alert';

import {
  StatusPillComponent,
  type StatusTone,
} from '../../../shared/ui/chips/status-pill.component';
import type {
  ConnectionHealth,
  ConnectionHealthState,
  CredentialHealth,
} from './connection-health';

@Component({
  selector: 'app-connection-health',
  imports: [DatePipe, StatusPillComponent, HlmCardImports, HlmAlertImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block min-w-0',
    'data-testid': 'connection-health',
    '[attr.data-health-state]': 'health().state',
  },
  template: `
    <section
      hlmCard
      class="gap-(--eg-space-4) p-(--eg-space-6) [overflow-wrap:anywhere] [&_p]:m-0 [&_p]:text-sm [&_p]:text-muted-foreground"
      aria-labelledby="connection-health-title"
    >
      <header class="flex flex-wrap items-center justify-between gap-(--eg-space-3)">
        <h3
          hlmCardTitle
          [size]="subsectionHeadingSize"
          class="m-0"
          id="connection-health-title"
          [textContent]="title"
        ></h3>
        <app-status-pill [tone]="tones[health().state]" [label]="stateLabels[health().state]" />
      </header>
      <p>{{ reasonLabels[health().reason] }}</p>
      @if (health().credentials.length > 0) {
        <ul class="m-0 grid list-none gap-(--eg-space-4) p-0">
          @for (row of rows(); track row.credential.kind + ':' + row.credential.id) {
            <li
              class="grid min-w-0 gap-(--eg-space-2) border-t border-border pt-(--eg-space-4) [overflow-wrap:anywhere]"
              data-testid="connection-health-credential"
            >
              <div class="flex flex-wrap items-center justify-between gap-(--eg-space-3)">
                <strong class="min-w-0">{{ row.credential.label }}</strong>
                <app-status-pill
                  [tone]="credentialTone(row.credential)"
                  [label]="statusLabels[row.credential.status]"
                />
              </div>
              <p>{{ detailLabels[row.credential.detail] }}</p>
              @if (row.expiresAt) {
                <p>
                  {{ expiresLabel }}
                  <time [attr.datetime]="row.expiresAt">{{
                    row.expiresAt | date: 'yyyy-MM-dd HH:mm:ss' : '+0000'
                  }}</time>
                </p>
              }
              @if (row.overlapEndsAt) {
                <p>
                  {{ overlapLabel }}
                  <time [attr.datetime]="row.overlapEndsAt">{{
                    row.overlapEndsAt | date: 'yyyy-MM-dd HH:mm:ss' : '+0000'
                  }}</time>
                </p>
              }
            </li>
          }
        </ul>
      }
      <p
        class="grid gap-(--eg-space-2) border-t border-border pt-(--eg-space-4)"
        data-testid="connection-health-last-use"
      >
        <strong>{{ lastUseLabel }}</strong>
        @if (health().lastTokenUseAt; as lastUse) {
          <time [attr.datetime]="lastUse">{{
            lastUse | date: 'yyyy-MM-dd HH:mm:ss' : '+0000'
          }}</time>
        } @else {
          <span>{{ notRecorded }}</span>
        }
      </p>
      <p>{{ lastUseHint }}</p>
      @if (health().lastTokenUseIsLowerBound) {
        <p>{{ lowerBoundHint }}</p>
      }
      @if (bindingsTruncated() || tokensTruncated()) {
        <p hlmAlert role="note" class="text-sm">{{ truncationHint }}</p>
      }
    </section>
  `,
})
export class ConnectionHealthComponent {
  readonly health = input.required<ConnectionHealth>();
  readonly tokens = input.required<readonly IngestTokenView[]>();
  readonly bindingsTruncated = input(false);
  readonly tokensTruncated = input(false);
  protected readonly subsectionHeadingSize = 'subsection' as const;

  protected readonly rows = computed(() => {
    const tokens = new Map(this.tokens().map((token) => [token.id, token]));
    return this.health().credentials.map((credential) => {
      const token = credential.kind === 'token' ? tokens.get(credential.id) : undefined;
      return {
        credential,
        expiresAt: token?.expires_at ?? null,
        overlapEndsAt: token?.status === 'rotated' ? (token.overlap_ends_at ?? null) : null,
      };
    });
  });

  protected readonly title = $localize`:@@onboarding.health.title:Connection health`;
  protected readonly expiresLabel = $localize`:@@onboarding.health.expires:Expires (UTC):`;
  protected readonly overlapLabel = $localize`:@@onboarding.health.overlap:Rotation overlap ends (UTC):`;
  protected readonly lastUseLabel = $localize`:@@onboarding.health.last-use:Last successful token use (UTC)`;
  protected readonly notRecorded = $localize`:@@onboarding.health.not-recorded:Not recorded`;
  protected readonly lastUseHint = $localize`:@@onboarding.health.last-use-hint:Token use is not a project-wide ingest timestamp. OIDC-only activity is not shown, and token-use timestamps are updated at most once every 60 seconds.`;
  protected readonly lowerBoundHint = $localize`:@@onboarding.health.lower-bound:The token list is incomplete. The latest token use shown may be older than the newest use; an unlisted token may have been used more recently.`;
  protected readonly truncationHint = $localize`:@@onboarding.health.truncated:Only part of the credential list is shown. Health reflects the listed credentials; an unlisted credential may still be usable.`;
  protected readonly stateLabels: Record<ConnectionHealthState, string> = {
    healthy: $localize`:@@onboarding.health.state-healthy:Healthy`,
    pending: $localize`:@@onboarding.health.state-pending:Pending`,
    unhealthy: $localize`:@@onboarding.health.state-unhealthy:Unhealthy`,
    unknown: $localize`:@@onboarding.health.state-unknown:Unknown`,
  };
  protected readonly tones: Record<ConnectionHealthState, StatusTone> = {
    healthy: 'success',
    pending: 'warning',
    unhealthy: 'danger',
    unknown: 'neutral',
  };
  protected readonly reasonLabels: Record<ConnectionHealth['reason'], string> = {
    usable: $localize`:@@onboarding.health.reason-usable:At least one listed credential is usable. This does not confirm a successful ingest.`,
    'rotating-only': $localize`:@@onboarding.health.reason-rotating:Only tokens in their rotation overlap are usable. Update CI to use the replacement before the overlap ends.`,
    'pending-verification': $localize`:@@onboarding.health.reason-pending:No listed credential is usable yet. Run the first CI exchange to verify the pending binding.`,
    truncated: $localize`:@@onboarding.health.reason-truncated:No usable credential is shown, but the list is incomplete. Connection health cannot be determined from this list.`,
    'all-unusable': $localize`:@@onboarding.health.reason-unusable:None of the listed credentials is usable. Create an ingest token or enable a CI binding.`,
    'no-credentials': $localize`:@@onboarding.health.reason-empty:No connection credentials are recorded. Create a CI binding or an ingest token.`,
  };
  protected readonly statusLabels: Record<
    CiBindingView['status'] | IngestTokenView['status'],
    string
  > = {
    active: $localize`:@@onboarding.health.status-active:Active`,
    pending_verification: $localize`:@@onboarding.health.status-awaiting:Awaiting CI`,
    disabled: $localize`:@@onboarding.health.status-disabled:Disabled`,
    rotated: $localize`:@@onboarding.health.status-rotated:Rotated`,
    expired: $localize`:@@onboarding.health.status-expired:Expired`,
    revoked: $localize`:@@onboarding.health.status-revoked:Revoked`,
  };
  protected readonly detailLabels: Record<CredentialHealth['detail'], string> = {
    active: $localize`:@@onboarding.health.detail-active:Usable.`,
    rotating: $localize`:@@onboarding.health.detail-rotating:Usable during the rotation overlap. Switch CI to the replacement token before it ends.`,
    pending: $localize`:@@onboarding.health.detail-pending:Not usable until the first CI exchange verifies this binding.`,
    expired: $localize`:@@onboarding.health.detail-expired:Not usable: the token has expired.`,
    revoked: $localize`:@@onboarding.health.detail-revoked:Not usable: the token has been revoked.`,
    disabled: $localize`:@@onboarding.health.detail-disabled:Not usable: the binding is disabled.`,
    'rotated-ended': $localize`:@@onboarding.health.detail-rotated-ended:Not usable: the rotation overlap has ended. Use the replacement token.`,
  };

  protected credentialTone(credential: CredentialHealth): StatusTone {
    if (credential.detail === 'rotating' || credential.detail === 'pending') return 'warning';
    return credential.usable ? 'success' : 'danger';
  }
}
