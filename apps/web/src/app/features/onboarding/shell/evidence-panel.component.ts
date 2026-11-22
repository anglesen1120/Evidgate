import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
  output,
} from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';
import {
  OnboardingRecordButtonComponent,
  OnboardingTableRangeComponent,
  RECORD_COPY,
  type OnboardingRecordView,
} from '../onboarding-record-view.component';

import { shortDateFormat } from '../../../shared/time/short-date-format';
import type { OnboardingProgress } from '../data/onboarding-progress.service';
import { activationInstantLabel } from '../steps/default-policy';

interface LedgerRow {
  key: string;
  resource: string;
  value: string | number;
  identifier?: string;
  truncated?: boolean;
  activated?: boolean;
}

@Component({
  selector: 'app-onboarding-evidence-panel',
  imports: [
    DatePipe,
    DecimalPipe,
    HlmCardImports,
    HlmTableImports,
    OnboardingRecordButtonComponent,
    OnboardingTableRangeComponent,
  ],
  host: { class: 'block min-w-0' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section
      hlmCard
      class="min-w-0 gap-0 py-0"
      data-testid="onboarding-evidence-panel"
      aria-labelledby="onboarding-evidence-title"
    >
      <header
        hlmCardHeader
        class="min-w-0 border-b border-border p-(--eg-space-5) md:px-(--eg-space-6)"
      >
        <h2 hlmCardTitle [size]="sectionHeadingSize" id="onboarding-evidence-title">{{ title }}</h2>
        <p hlmCardDescription class="m-0 text-sm">{{ sourceLabel }}</p>
      </header>
      <div
        hlmCardContent
        class="grid min-w-0 gap-(--eg-space-4) p-(--eg-space-5) md:px-(--eg-space-6)"
      >
        @if (progress(); as resources) {
          @if (resources.project; as createdProject) {
            <div hlmTableContainer class="min-w-0 max-w-full">
              <table hlmTable class="[&_th]:whitespace-normal [&_td]:whitespace-normal">
                <caption hlmCaption class="caption-top text-start">
                  {{
                    title
                  }}
                </caption>
                <thead hlmTHead>
                  <tr hlmTr>
                    <th hlmTh scope="col">{{ resourceLabel }}</th>
                    <th hlmTh scope="col">{{ confirmedLabel }}</th>
                    <th hlmTh scope="col">{{ copy.actions }}</th>
                  </tr>
                </thead>
                <tbody hlmTBody>
                  @for (row of ledgerRows(); track row.key) {
                    <tr hlmTr class="h-(--eg-space-11)">
                      <th hlmTh scope="row" class="font-semibold text-foreground">
                        {{ row.resource }}
                      </th>
                      <td hlmTd [class.text-end]="typeof row.value === 'number'">
                        @if (typeof row.value === 'number') {
                          <span class="tabular-nums"
                            >{{ row.value | number }}{{ row.truncated ? '+' : '' }}</span
                          >
                        } @else {
                          <bdi>{{ row.value }}</bdi>
                        }
                        @if (row.identifier) {
                          <bdi class="block font-mono text-xs text-muted-foreground">{{
                            row.identifier
                          }}</bdi>
                        }
                        @if (row.activated) {
                          <div class="text-xs text-muted-foreground">
                            {{ activatedAtLabel }}:
                            @if (activatedAt(); as at) {
                              <time [attr.datetime]="at">{{
                                at | date: dateTimeFormat : '+0000'
                              }}</time>
                            } @else {
                              {{ unavailable }}
                            }
                          </div>
                        }
                      </td>
                      <td hlmTd>
                        <app-onboarding-record
                          [subject]="typeof row.value === 'number' ? row.resource : row.value"
                          [appOnboardingRecord]="facts"
                          [recordTitle]="copy.resource"
                          [recordId]="row.identifier"
                          (inspect)="viewRecord.emit($event)"
                        />
                        <ng-template #facts
                          ><dl>
                            <div>
                              <dt>{{ projectLabel }}</dt>
                              <dd>
                                <bdi>{{ createdProject.name }}</bdi>
                              </dd>
                            </div>
                            <div>
                              <dt>{{ identifierLabel }}</dt>
                              <dd>
                                <bdi class="font-mono">{{ createdProject.id }}</bdi>
                              </dd>
                            </div>
                            @if (row.key !== 'project') {
                              <div>
                                <dt>{{ row.resource }}</dt>
                                <dd>
                                  @if (typeof row.value === 'number') {
                                    {{ row.value | number }}{{ row.truncated ? '+' : '' }}
                                  } @else {
                                    <bdi class="font-mono">{{ row.value }}</bdi>
                                  }
                                </dd>
                              </div>
                            }
                            @if (row.activated) {
                              <div>
                                <dt>{{ activatedAtLabel }}</dt>
                                <dd>
                                  @if (activatedAt(); as at) {
                                    <time [attr.datetime]="at">{{
                                      at | date: dateTimeFormat : '+0000'
                                    }}</time>
                                  } @else {
                                    {{ unavailable }}
                                  }
                                </dd>
                              </div>
                            }
                          </dl>
                          @if (refreshFailed()) {
                            <p>{{ stale }}</p>
                          }
                        </ng-template>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <app-onboarding-table-range [appOnboardingTableRange]="ledgerRows().length" />
            @if (resources.activation.kind === 'forbidden') {
              <p
                class="m-0 text-sm text-muted-foreground"
                [attr.data-resource-state]="'unavailable'"
              >
                {{ activationUnavailable }}
              </p>
            }
          } @else {
            <p class="m-0 text-sm text-muted-foreground" [attr.data-resource-state]="'empty'">
              {{ empty }}
            </p>
          }
        } @else {
          <p class="m-0 text-sm text-muted-foreground" [attr.data-resource-state]="'unavailable'">
            {{ unavailable }}
          </p>
        }
        @if (refreshFailed()) {
          <p
            class="m-0 rounded-(--eg-radius-sm) bg-muted p-(--eg-space-4) text-sm text-foreground"
            role="status"
            [attr.data-resource-state]="'stale'"
          >
            {{ stale }}
          </p>
        }
      </div>
    </section>
  `,
})
export class EvidencePanelComponent {
  protected readonly dateTimeFormat = shortDateFormat(inject(LOCALE_ID), true, 'medium');

  readonly progress = input<OnboardingProgress | null>(null);
  readonly refreshFailed = input(false);
  readonly viewRecord = output<OnboardingRecordView>();
  protected readonly copy = RECORD_COPY;
  protected readonly resourceLabel = $localize`:@@onboarding.record.resource-kind:Resource`;
  protected readonly confirmedLabel = $localize`:@@onboarding.record.confirmed-value:Confirmed value`;
  protected readonly ledgerRows = computed<LedgerRow[]>(() => {
    const data = this.progress();
    if (!data?.project) return [];
    const rows: LedgerRow[] = [
      {
        key: 'project',
        resource: this.projectLabel,
        value: data.project.name,
        identifier: data.project.id,
      },
    ];
    if (data.bindings.length)
      rows.push({
        key: 'bindings',
        resource: this.bindingsLabel,
        value: data.bindings.length,
        truncated: data.bindingsTruncated,
      });
    if (data.tokens.length)
      rows.push({
        key: 'tokens',
        resource: this.tokensLabel,
        value: data.tokens.length,
        truncated: data.tokensTruncated,
      });
    if (data.sampleRun) {
      rows.push({ key: 'run', resource: this.sampleLabel, value: data.sampleRun.run_id });
      if (data.sampleRun.current_evidence_revision_id)
        rows.push({
          key: 'evidence',
          resource: this.evidenceLabel,
          value: data.sampleRun.current_evidence_revision_id,
        });
    }
    if (data.activation.kind === 'active')
      rows.push({
        key: 'activation',
        resource: this.activationLabel,
        value: data.activation.state.policy_revision_id,
        activated: true,
      });
    return rows;
  });
  protected readonly sectionHeadingSize = 'section' as const;
  protected readonly sourceLabel = $localize`:@@onboarding.evidence.source:Read from server-confirmed resources, not browser completion flags.`;
  protected readonly activatedAt = computed(() =>
    activationInstantLabel(this.activation()?.activated_at),
  );
  protected readonly activation = computed(() => {
    const activation = this.progress()?.activation;
    return activation?.kind === 'active' ? activation.state : null;
  });
  protected readonly title = $localize`:@@onboarding.evidence.title:Created resources`;
  protected readonly projectLabel = $localize`:@@onboarding.evidence.project:Project`;
  protected readonly identifierLabel = $localize`:@@onboarding.evidence.identifier:Project ID`;
  protected readonly empty = $localize`:@@onboarding.evidence.no-resources:No setup resources have been created yet.`;
  protected readonly unavailable = $localize`:@@onboarding.evidence.unavailable:Created resources are unavailable.`;
  protected readonly stale = $localize`:@@onboarding.evidence.stale:Resources could not be refreshed. Showing the last confirmed resources.`;
  protected readonly bindingsLabel = $localize`:@@onboarding.evidence.bindings:CI bindings`;
  protected readonly tokensLabel = $localize`:@@onboarding.evidence.tokens:Ingest tokens`;
  protected readonly sampleLabel = $localize`:@@onboarding.evidence.sample:Sample run ID`;
  protected readonly evidenceLabel = $localize`:@@onboarding.evidence.revision:Evidence revision ID`;
  protected readonly activationLabel = $localize`:@@onboarding.evidence.activation:Activated policy revision`;
  protected readonly activatedAtLabel = $localize`:@@onboarding.evidence.activated-at:Activated at (UTC)`;
  protected readonly activationUnavailable = $localize`:@@onboarding.evidence.activation-unavailable:Activation details are unavailable with your current access.`;
}
