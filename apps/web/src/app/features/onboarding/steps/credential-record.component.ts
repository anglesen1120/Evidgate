import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
} from '@angular/core';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { shortDateFormat } from '../../../shared/time/short-date-format';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { RECORD_COPY } from '../onboarding-record-view.component';

export const CREDENTIAL_STATUS = {
  pending_verification: $localize`:@@onboarding.connect.awaiting-ci:Awaiting CI`,
  active: $localize`:@@onboarding.connect.active:Active`,
  disabled: $localize`:@@onboarding.connect.disabled:Disabled`,
  rotated: $localize`:@@onboarding.connect.rotated:Rotated`,
  expired: $localize`:@@onboarding.connect.expired:Expired`,
  revoked: $localize`:@@onboarding.connect.revoked:Revoked`,
};

@Component({
  selector: 'app-onboarding-credential-record',
  imports: [DatePipe, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dl>
      <div>
        <dt>{{ copy.identifier }}</dt>
        <dd>
          <bdi class="font-mono">{{ credential().id }}</bdi>
        </dd>
      </div>
      <div>
        <dt>{{ copy.status }}</dt>
        <dd><app-status-pill [tone]="'neutral'" [label]="statuses[credential().status]" /></dd>
      </div>
      @if (binding(); as binding) {
        <div>
          <dt>{{ repository }}</dt>
          <dd>
            <bdi>{{ binding.repository_full_name }}</bdi>
          </dd>
        </div>
        <div>
          <dt>{{ copy.source }}</dt>
          <dd>
            <bdi class="font-mono">{{ binding.provider }}</bdi>
          </dd>
        </div>
        <div>
          <dt>{{ repositoryId }}</dt>
          <dd>
            <bdi class="font-mono">{{ binding.repository_id }}</bdi>
          </dd>
        </div>
        <div>
          <dt>{{ refs }}</dt>
          <dd>
            @for (ref of binding.allowed_ref_patterns; track $index) {
              <bdi class="block font-mono">{{ ref }}</bdi>
            } @empty {
              {{ none }}
            }
          </dd>
        </div>
        <div>
          <dt>{{ workflows }}</dt>
          <dd>
            @for (workflow of binding.allowed_workflow_refs; track $index) {
              <bdi class="block font-mono">{{ workflow }}</bdi>
            } @empty {
              {{ none }}
            }
          </dd>
        </div>
      }
      @if (token(); as token) {
        <div>
          <dt>{{ name }}</dt>
          <dd>
            <bdi>{{ token.name }}</bdi>
          </dd>
        </div>
        <div>
          <dt>{{ prefix }}</dt>
          <dd>
            <code
              ><bdi>{{ token.prefix }}</bdi></code
            >
          </dd>
        </div>
        <div>
          <dt>{{ scopes }}</dt>
          <dd>
            <bdi>{{ token.scopes.join(', ') || none }}</bdi>
          </dd>
        </div>
      }
    </dl>
    <h3 class="my-(--eg-space-4) border-t border-border pt-(--eg-space-4) text-sm font-semibold">
      {{ copy.history }}
    </h3>
    <dl>
      <div>
        <dt>{{ created }}</dt>
        <dd>
          <time [attr.datetime]="credential().created_at">{{
            credential().created_at | date: format : '+0000'
          }}</time>
        </dd>
      </div>
      @if (token(); as token) {
        <div>
          <dt>{{ expires }}</dt>
          <dd>
            @if (token.expires_at; as instant) {
              <time [attr.datetime]="instant">{{ instant | date: format : '+0000' }}</time>
            } @else {
              {{ none }}
            }
          </dd>
        </div>
        <div>
          <dt>{{ lastUsed }}</dt>
          <dd>
            @if (token.last_used_at; as instant) {
              <time [attr.datetime]="instant">{{ instant | date: format : '+0000' }}</time>
            } @else {
              {{ none }}
            }
          </dd>
        </div>
      }
    </dl>
  `,
})
export class CredentialRecordComponent {
  protected readonly format = shortDateFormat(inject(LOCALE_ID), true, 'medium');
  readonly credential = input.required<CiBindingView | IngestTokenView>();
  protected readonly binding = computed(() => {
    const row = this.credential();
    return 'repository_full_name' in row ? row : null;
  });
  protected readonly token = computed(() => {
    const row = this.credential();
    return 'prefix' in row ? row : null;
  });
  protected readonly copy = RECORD_COPY;
  protected readonly statuses = CREDENTIAL_STATUS;
  protected readonly name = $localize`:@@onboarding.connect.token-name:Token name`;
  protected readonly repository = $localize`:@@onboarding.connect.repo-name:Repository (owner/repo)`;
  protected readonly repositoryId = $localize`:@@onboarding.connect.repo-id:GitHub numeric repository ID`;
  protected readonly refs = $localize`:@@onboarding.connect.refs:Allowed Git ref patterns`;
  protected readonly workflows = $localize`:@@onboarding.connect.workflows:Allowed workflow refs`;
  protected readonly prefix = $localize`:@@onboarding.connect.prefix:Prefix`;
  protected readonly scopes = $localize`:@@onboarding.connect.scopes:Scopes`;
  protected readonly expires = $localize`:@@onboarding.connect.expires:Expires (UTC)`;
  protected readonly lastUsed = $localize`:@@onboarding.connect.last-used:Last used (UTC)`;
  protected readonly created = $localize`:@@onboarding.connect.created:Created (UTC)`;
  protected readonly none = $localize`:@@onboarding.connect.not-set:Not set`;
}
