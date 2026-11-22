import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  LOCALE_ID,
  output,
} from '@angular/core';
import type { CiBindingView } from '@evidgate/sdk';
import { HlmTableImports } from '@evidgate/ui/table';
import { shortDateFormat } from '../../../shared/time/short-date-format';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  OnboardingRecordButtonComponent,
  OnboardingTableRangeComponent,
  RECORD_COPY,
  type OnboardingRecordView,
} from '../onboarding-record-view.component';
import { CREDENTIAL_STATUS, CredentialRecordComponent } from './credential-record.component';

@Component({
  selector: 'app-connect-ci-binding-table',
  imports: [
    DatePipe,
    StatusPillComponent,
    HlmTableImports,
    OnboardingRecordButtonComponent,
    OnboardingTableRangeComponent,
    CredentialRecordComponent,
  ],
  host: { class: 'block min-w-0' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div hlmTableContainer class="min-w-0 max-w-full">
      <table hlmTable class="[&_th]:whitespace-normal [&_td]:whitespace-normal">
        <caption hlmCaption class="caption-top text-start">
          {{
            bindingsLabel
          }}
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">{{ repository }}</th>
            <th hlmTh scope="col">{{ recordCopy.status }}</th>
            <th hlmTh scope="col">{{ createdLabel }}</th>
            <th hlmTh scope="col">{{ recordCopy.actions }}</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          @for (binding of bindings(); track binding.id) {
            <tr
              hlmTr
              class="h-(--eg-space-11)"
              data-testid="connect-binding-row"
              [attr.data-status]="binding.status"
            >
              <th hlmTh scope="row">
                <bdi class="font-semibold text-foreground">{{ binding.repository_full_name }}</bdi
                ><bdi
                  class="block max-w-[24ch] truncate font-mono text-xs font-normal text-muted-foreground"
                  >{{ binding.id }}</bdi
                >
              </th>
              <td hlmTd>
                <app-status-pill [tone]="'neutral'" [label]="statusLabels[binding.status]" />
              </td>
              <td hlmTd>
                <time [attr.datetime]="binding.created_at">{{
                  binding.created_at | date: dateTimeFormat : '+0000'
                }}</time>
              </td>
              <td hlmTd>
                <app-onboarding-record
                  [appOnboardingRecord]="facts"
                  [recordTitle]="recordCopy.binding"
                  [subject]="binding.repository_full_name"
                  [recordId]="binding.id"
                  [recordStatus]="statusLabels[binding.status]"
                  (inspect)="viewRecord.emit($event)"
                />
                <ng-template #facts
                  ><app-onboarding-credential-record [credential]="binding"
                /></ng-template>
              </td>
            </tr>
          } @empty {
            <tr hlmTr>
              <td hlmTd colspan="4">{{ emptyBindings }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
    <app-onboarding-table-range
      [appOnboardingTableRange]="bindings().length"
      [truncated]="truncated()"
    />
  `,
})
export class ConnectCiBindingTableComponent {
  protected readonly dateTimeFormat = shortDateFormat(inject(LOCALE_ID), true, 'medium');
  readonly bindings = input.required<readonly CiBindingView[]>();
  readonly truncated = input.required<boolean>();
  readonly viewRecord = output<OnboardingRecordView>();
  protected readonly recordCopy = RECORD_COPY;
  protected readonly statusLabels = CREDENTIAL_STATUS;
  protected readonly bindingsLabel = $localize`:@@onboarding.connect.bindings:CI bindings`;
  protected readonly repository = $localize`:@@onboarding.connect.repo-name:Repository (owner/repo)`;
  protected readonly createdLabel = $localize`:@@onboarding.connect.created:Created (UTC)`;
  protected readonly emptyBindings = $localize`:@@onboarding.connect.empty-bindings:No CI bindings yet.`;
}
