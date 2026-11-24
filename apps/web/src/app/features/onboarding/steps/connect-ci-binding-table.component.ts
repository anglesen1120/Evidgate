import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
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
    LoadingPatternComponent,
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
    <app-loading-pattern [layout]="'table'" [busy]="fetching()" [hasRows]="bindings().length > 0" />
    <div hlmTableContainer class="min-w-0 max-w-full">
      <table [attr.aria-busy]="fetching()" hlmTable class="wrap-normal">
        <caption hlmCaption class="caption-top text-start">
          {{
            bindingsLabel
          }}
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">{{ repository }}</th>
            <th hlmTh scope="col" class="w-px whitespace-nowrap">{{ recordCopy.actions }}</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          @for (binding of bindings(); track binding.id) {
            <tr
              hlmTr
              class="*:align-top"
              data-testid="connect-binding-row"
              [attr.data-status]="binding.status"
            >
              <th hlmTh scope="row" class="font-normal text-foreground">
                <bdi class="font-semibold wrap-anywhere text-foreground">{{
                  binding.repository_full_name
                }}</bdi
                ><bdi
                  class="block w-0 min-w-full truncate font-mono text-xs text-muted-foreground"
                  >{{ binding.id }}</bdi
                >
                <dl
                  class="mt-(--eg-space-3) grid gap-(--eg-space-2) [&>div]:flex [&>div]:flex-wrap [&>div]:items-center [&>div]:gap-x-(--eg-space-3) [&_dt]:text-muted-foreground [&_dd]:m-0"
                >
                  <div>
                    <dt>{{ recordCopy.status }}</dt>
                    <dd>
                      <app-status-pill [tone]="'neutral'" [label]="statusLabels[binding.status]" />
                    </dd>
                  </div>
                  <div>
                    <dt>{{ createdLabel }}</dt>
                    <dd>
                      <time class="wrap-normal" [attr.datetime]="binding.created_at">{{
                        binding.created_at | date: dateTimeFormat : '+0000'
                      }}</time>
                    </dd>
                  </div>
                </dl>
              </th>
              <td hlmTd class="[&_button]:whitespace-nowrap">
                <app-onboarding-record
                  [appOnboardingRecord]="facts"
                  [recordSize]="'lg'"
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
              <td hlmTd colspan="2">{{ emptyBindings }}</td>
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
  readonly fetching = input(false);
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
