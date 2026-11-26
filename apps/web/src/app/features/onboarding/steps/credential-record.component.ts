import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, LOCALE_ID } from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { shortDateFormat } from '../../../shared/time/short-date-format';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { RECORD_COPY } from '../onboarding-record-view.component';

export const CREDENTIAL_STATUS = {
  active: $localize`:@@onboarding.connect.active:Active`,
  rotated: $localize`:@@onboarding.connect.rotated:Rotated`,
  expired: $localize`:@@onboarding.connect.expired:Expired`,
  revoked: $localize`:@@onboarding.connect.revoked:Revoked`,
};

@Component({
  selector: 'app-onboarding-credential-record',
  imports: [DatePipe, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dl class="hlm-field-grid">
      <div class="hlm-field-wide">
        <dt>{{ copy.identifier }}</dt>
        <dd>
          <bdi class="font-mono">{{ credential().id }}</bdi>
        </dd>
      </div>
      <div>
        <dt>{{ copy.status }}</dt>
        <dd><app-status-pill [tone]="'neutral'" [label]="statuses[credential().status]" /></dd>
      </div>
      <div>
        <dt>{{ name }}</dt>
        <dd>
          <bdi>{{ credential().name }}</bdi>
        </dd>
      </div>
      <div>
        <dt>{{ prefix }}</dt>
        <dd>
          <code
            ><bdi>{{ credential().prefix }}</bdi></code
          >
        </dd>
      </div>
      <div class="hlm-field-wide">
        <dt>{{ scopes }}</dt>
        <dd>
          <bdi>{{ credential().scopes.join(', ') || none }}</bdi>
        </dd>
      </div>
    </dl>
    <h3 class="my-(--eg-space-4) border-t border-border pt-(--eg-space-4) text-sm font-semibold">
      {{ copy.history }}
    </h3>
    <dl class="hlm-field-grid">
      <div>
        <dt>{{ created }}</dt>
        <dd>
          <time [attr.datetime]="credential().created_at">{{
            credential().created_at | date: format : '+0000'
          }}</time>
        </dd>
      </div>
      <div>
        <dt>{{ expires }}</dt>
        <dd>
          @if (credential().expires_at; as instant) {
            <time [attr.datetime]="instant">{{ instant | date: format : '+0000' }}</time>
          } @else {
            {{ none }}
          }
        </dd>
      </div>
      <div>
        <dt>{{ lastUsed }}</dt>
        <dd>
          @if (credential().last_used_at; as instant) {
            <time [attr.datetime]="instant">{{ instant | date: format : '+0000' }}</time>
          } @else {
            {{ none }}
          }
        </dd>
      </div>
    </dl>
  `,
})
export class CredentialRecordComponent {
  protected readonly format = shortDateFormat(inject(LOCALE_ID), true, 'medium');
  readonly credential = input.required<IngestTokenView>();
  protected readonly copy = RECORD_COPY;
  protected readonly statuses = CREDENTIAL_STATUS;
  protected readonly name = $localize`:@@onboarding.connect.token-name:Token name`;
  protected readonly prefix = $localize`:@@onboarding.connect.prefix:Prefix`;
  protected readonly scopes = $localize`:@@onboarding.connect.scopes:Scopes`;
  protected readonly expires = $localize`:@@onboarding.connect.expires:Expires (UTC)`;
  protected readonly lastUsed = $localize`:@@onboarding.connect.last-used:Last used (UTC)`;
  protected readonly created = $localize`:@@onboarding.connect.created:Created (UTC)`;
  protected readonly none = $localize`:@@onboarding.connect.not-set:Not set`;
}
