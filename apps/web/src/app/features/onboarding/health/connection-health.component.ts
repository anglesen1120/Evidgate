import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
  output,
} from '@angular/core';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmTableImports } from '@evidgate/ui/table';
import {
  OnboardingRecordButtonComponent,
  OnboardingTableRangeComponent,
  RECORD_COPY,
  type OnboardingRecordView,
} from '../onboarding-record-view.component';

import { shortDateFormat } from '../../../shared/time/short-date-format';

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
  imports: [
    DatePipe,
    NgTemplateOutlet,
    StatusPillComponent,
    HlmCardImports,
    HlmAlertImports,
    HlmTableImports,
    OnboardingRecordButtonComponent,
    OnboardingTableRangeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block min-w-0',
    'data-testid': 'connection-health',
    '[attr.data-health-state]': 'health().state',
  },
  template: `
    <section
      hlmCard
      class="min-w-0 gap-(--eg-space-4) p-(--eg-space-4) wrap-anywhere md:p-(--eg-space-5) [&_p]:m-0 [&_p]:text-sm [&_p]:text-muted-foreground"
      aria-labelledby="connection-health-title"
    >
      <header
        class="flex min-w-0 flex-wrap items-center justify-between gap-(--eg-space-3) border-b border-border pb-(--eg-space-4)"
      >
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
      <div hlmTableContainer class="min-w-0 max-w-full">
        <table hlmTable class="[&_th]:whitespace-normal [&_td]:whitespace-normal">
          <caption hlmCaption class="caption-top text-start">
            {{
              credentialHealth
            }}
          </caption>
          <thead hlmTHead>
            <tr hlmTr>
              <th hlmTh scope="col">{{ copy.credential }}</th>
              <th hlmTh scope="col">{{ copy.status }}</th>
              <th hlmTh scope="col">{{ copy.actions }}</th>
            </tr>
          </thead>
          <tbody hlmTBody>
            @for (row of rows(); track row.credential.kind + ':' + row.credential.id) {
              <tr hlmTr class="h-(--eg-space-11)" data-testid="connection-health-credential">
                <th hlmTh scope="row">
                  <bdi class="font-semibold text-foreground">{{ row.credential.label }}</bdi
                  ><span class="block text-xs font-normal text-muted-foreground">{{
                    row.credential.kind === 'token' ? tokenKind : bindingKind
                  }}</span>
                </th>
                <td hlmTd>
                  <app-status-pill
                    [tone]="credentialTone(row.credential)"
                    [label]="statusLabels[row.credential.status]"
                  />
                  <p>{{ detailLabels[row.credential.detail] }}</p>
                  <ng-container [ngTemplateOutlet]="timing" />
                </td>
                <td hlmTd>
                  <app-onboarding-record
                    [appOnboardingRecord]="facts"
                    [subject]="row.credential.label"
                    [recordTitle]="copy.health"
                    [recordId]="row.credential.id"
                    [recordStatus]="statusLabels[row.credential.status]"
                    [recordTone]="credentialTone(row.credential)"
                    (inspect)="viewRecord.emit($event)"
                  />
                  <ng-template #facts>
                    <dl class="hlm-field-grid">
                      <div>
                        <dt>{{ copy.credential }}</dt>
                        <dd>{{ row.credential.kind === 'token' ? tokenKind : bindingKind }}</dd>
                      </div>
                      <div class="hlm-field-wide">
                        <dt>{{ copy.identifier }}</dt>
                        <dd>
                          <bdi class="font-mono">{{ row.credential.id }}</bdi>
                        </dd>
                      </div>
                      <div class="hlm-field-wide">
                        <dt>{{ copy.status }}</dt>
                        <dd>
                          <app-status-pill
                            [tone]="credentialTone(row.credential)"
                            [label]="statusLabels[row.credential.status]"
                          />
                          <p>{{ detailLabels[row.credential.detail] }}</p>
                        </dd>
                      </div>
                    </dl>
                    <ng-container [ngTemplateOutlet]="timing" />
                  </ng-template>
                  <ng-template #timing>
                    @if (row.expiresAt; as expiry) {
                      <p>
                        {{ expiresLabel }}
                        <time [attr.datetime]="expiry">{{
                          expiry | date: dateTimeFormat : '+0000'
                        }}</time>
                      </p>
                    }
                    @if (row.overlapEndsAt; as end) {
                      <p>
                        {{ overlapLabel }}
                        <time [attr.datetime]="end">{{
                          end | date: dateTimeFormat : '+0000'
                        }}</time>
                      </p>
                    }
                  </ng-template>
                </td>
              </tr>
            } @empty {
              <tr hlmTr>
                <td hlmTd colspan="3">{{ notRecorded }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <app-onboarding-table-range
        [appOnboardingTableRange]="rows().length"
        [truncated]="bindingsTruncated() || tokensTruncated()"
      />
      <p
        class="grid min-w-0 gap-(--eg-space-1) rounded-(--eg-radius-md) bg-muted p-(--eg-space-4) [&_strong]:text-foreground [&_time]:tabular-nums"
        data-testid="connection-health-last-use"
      >
        <strong>{{ lastUseLabel }}</strong>
        @if (health().lastTokenUseAt; as lastUse) {
          <time [attr.datetime]="lastUse">{{ lastUse | date: dateTimeFormat : '+0000' }}</time>
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
  protected readonly dateTimeFormat = shortDateFormat(inject(LOCALE_ID), true, '24h');

  readonly health = input.required<ConnectionHealth>();
  readonly tokens = input.required<readonly IngestTokenView[]>();
  readonly bindingsTruncated = input(false);
  readonly tokensTruncated = input(false);
  readonly viewRecord = output<OnboardingRecordView>();
  protected readonly copy = RECORD_COPY;
  protected readonly credentialHealth = $localize`:@@onboarding.record.health-caption:Credential health`;
  protected readonly tokenKind = $localize`:@@onboarding.connect.token-title:Ingest token`;
  protected readonly bindingKind = $localize`:@@onboarding.record.binding-kind:CI binding`;
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
    pending: 'neutral',
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
    if (credential.detail === 'pending') return 'neutral';
    if (credential.detail === 'rotating') return 'warning';
    return credential.usable ? 'success' : 'danger';
  }
}
