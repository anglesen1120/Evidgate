import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
  type TemplateRef,
} from '@angular/core';
import type { AiSettingsVersionSummary } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardTitle } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import {
  DataTableComponent,
  type DataTableColumn,
  type DataTableCellContext,
} from '../../../shared/ui/data-table';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { isApiRequestError } from '../../../core/http/api-error';
import { AiSettingsStore } from './ai-settings.store';
import { FIELDS, textOf } from './ai-settings.model';

@Component({
  selector: 'app-ai-settings-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCardTitle,
    HlmInput,
    HlmLabel,
    DataTableComponent,
    RequestStateComponent,
    AbsoluteTimeComponent,
    CopyableIdentifierComponent,
  ],
  template: `
    <section class="grid min-w-0 gap-6">
      <h2 hlmCardTitle [size]="'section'" i18n="@@authoring.aiSettings.historyTab">
        Version history
      </h2>
      <label hlmLabel for="ai-history-filter" i18n="@@authoring.aiSettings.filter"
        >Filter loaded versions by reason</label
      >
      <input
        hlmInput
        id="ai-history-filter"
        [value]="filter()"
        (input)="filter.set($event.target.value)"
      />
      @if (store.selectedVersion() === 0) {
        <p id="ai-compare-reason" i18n="@@authoring.aiSettings.compareReason">
          View a version before choosing a second version to compare.
        </p>
      }
      <ng-template #time let-row><app-absolute-time [value]="row.created_at" /></ng-template>
      <ng-template #author let-row
        ><app-copyable-identifier [value]="row.author_membership_id" [label]="authorLabel"
      /></ng-template>
      <ng-template #actions let-row>
        <div class="flex flex-wrap gap-2">
          <button
            hlmBtn
            [variant]="'outline'"
            type="button"
            data-ai-inspect
            (click)="store.selectVersion(row.version_no)"
            i18n="@@authoring.aiSettings.inspect"
          >
            View version {{ row.version_no }}
          </button>
          <button
            hlmBtn
            [variant]="'ghost'"
            type="button"
            [disabled]="store.selectedVersion() === 0"
            [attr.aria-describedby]="store.selectedVersion() === 0 ? 'ai-compare-reason' : null"
            (click)="store.compareWith(row.version_no)"
            i18n="@@authoring.aiSettings.compare"
          >
            Compare with {{ row.version_no }}
          </button>
        </div>
      </ng-template>
      <app-request-state
        [status]="store.history.isLoading() ? 'loading' : store.history.error() ? 'error' : 'ready'"
        [error]="historyError()"
        [empty]="rows().length === 0"
        [emptyTitle]="filter() ? noMatch : empty"
        [retryEligible]="true"
        (retry)="store.history.reload()"
      >
        <app-data-table
          [rows]="rows()"
          [columns]="columns()"
          [rowId]="rowId"
          [caption]="historyTitle"
        />
      </app-request-state>
      <nav class="flex flex-wrap gap-3" [attr.aria-label]="pagesLabel">
        <button
          hlmBtn
          [variant]="'outline'"
          type="button"
          [disabled]="!store.cursor() || store.history.isLoading()"
          (click)="store.firstPage(); filter.set('')"
          i18n="@@authoring.aiSettings.first"
        >
          First page
        </button>
        <button
          hlmBtn
          [variant]="'outline'"
          type="button"
          [disabled]="
            !store.historyPage()?.next_cursor || store.history.isLoading() || store.cursorCycle()
          "
          (click)="store.nextPage(); filter.set('')"
          i18n="@@authoring.aiSettings.next"
        >
          Next page
        </button>
      </nav>
      @if (store.cursorCycle()) {
        <p role="alert" i18n="@@authoring.aiSettings.cursorCycle">
          The server repeated a page cursor. Return to the first page to retry.
        </p>
      }
      @if (store.selectedVersion() > 0) {
        <app-request-state
          [status]="store.detail.isLoading() ? 'loading' : store.detail.error() ? 'error' : 'ready'"
          [error]="detailError()"
          [retryEligible]="true"
          (retry)="store.detail.reload()"
        >
          @if (store.versionDetail(); as version) {
            <section class="grid min-w-0 gap-4" data-ai-version-detail>
              <h3 hlmCardTitle [size]="'subsection'" i18n="@@authoring.aiSettings.immutable">
                Version {{ version.version_no }} — read-only
              </h3>
              <app-absolute-time [value]="version.created_at" />
              <app-copyable-identifier [value]="version.settings_id" [label]="settingsIdLabel" />
              <app-copyable-identifier [value]="version.content_hash" [label]="hashLabel" />
              <app-copyable-identifier
                [value]="version.author_membership_id"
                [label]="authorLabel"
              />
              <p>{{ version.reason || notSet }}</p>
              @if (version.template; as template) {
                <p>{{ template.code }} · {{ template.version }}</p>
              }
              <p i18n="@@authoring.aiSettings.restoreUnavailable">
                Restore is unavailable: this screen provides read-only history.
              </p>
              <dl class="grid min-w-0 gap-4 wrap-anywhere">
                @for (field of fields; track field.key) {
                  @if (field.key !== 'reason') {
                    <div>
                      <dt class="font-semibold">{{ field.label }}</dt>
                      <dd class="m-0 whitespace-pre-wrap">
                        {{ textOf(version.settings, field.key) }}
                      </dd>
                    </div>
                  }
                }
                <div>
                  <dt i18n="@@authoring.aiSettings.languages">Output languages</dt>
                  <dd>{{ version.settings.languages.join(', ') }}</dd>
                </div>
              </dl>
            </section>
          }
        </app-request-state>
      }
      @if (store.compareVersion() > 0 && store.selectedVersion() > 0) {
        <app-request-state
          [status]="
            store.comparison.isLoading() ? 'loading' : store.comparison.error() ? 'error' : 'ready'
          "
          [error]="comparisonError()"
          [retryEligible]="true"
          (retry)="store.comparison.reload()"
        >
          @if (store.versionDetail(); as before) {
            @if (store.comparedVersion(); as after) {
              <section class="grid min-w-0 gap-4" data-ai-diff>
                <h3 hlmCardTitle [size]="'subsection'" i18n="@@authoring.aiSettings.diffTitle">
                  Compare version {{ before.version_no }} with {{ after.version_no }}
                </h3>
                @for (field of fields; track field.key) {
                  @if (
                    field.key !== 'reason' &&
                    textOf(before.settings, field.key) !== textOf(after.settings, field.key)
                  ) {
                    <section class="grid min-w-0 gap-2 border-t border-border pt-4">
                      <h4>{{ field.label }}</h4>
                      <dl class="grid min-w-0 gap-4 md:grid-cols-2 wrap-anywhere">
                        <div>
                          <dt i18n="@@authoring.aiSettings.before">Before</dt>
                          <dd class="m-0 whitespace-pre-wrap">
                            {{ textOf(before.settings, field.key) }}
                          </dd>
                        </div>
                        <div>
                          <dt i18n="@@authoring.aiSettings.after">After</dt>
                          <dd class="m-0 whitespace-pre-wrap">
                            {{ textOf(after.settings, field.key) }}
                          </dd>
                        </div>
                      </dl>
                    </section>
                  }
                }
                <dl>
                  <dt i18n="@@authoring.aiSettings.languageDiff">
                    Output languages (before → after)
                  </dt>
                  <dd>
                    {{ before.settings.languages.join(', ') }} →
                    {{ after.settings.languages.join(', ') }}
                  </dd>
                </dl>
              </section>
            }
          }
        </app-request-state>
      }
    </section>
  `,
})
export class AiSettingsHistoryComponent {
  protected readonly store = inject(AiSettingsStore);
  protected readonly filter = signal('');
  protected readonly fields = FIELDS;
  protected readonly textOf = textOf;
  protected readonly authorLabel = $localize`:@@authoring.aiSettings.author:Author membership`;
  protected readonly settingsIdLabel = $localize`:@@authoring.aiSettings.settingsId:Settings identity`;
  protected readonly hashLabel = $localize`:@@authoring.aiSettings.hash:Content hash`;
  protected readonly historyTitle = $localize`:@@authoring.aiSettings.historyTab:Version history`;
  protected readonly pagesLabel = $localize`:@@authoring.aiSettings.pages:Version history pages`;
  protected readonly noMatch = $localize`:@@authoring.aiSettings.noMatch:No loaded versions match this filter`;
  protected readonly empty = $localize`:@@authoring.aiSettings.emptyHistory:No saved settings versions`;
  protected readonly notSet = $localize`:@@authoring.aiSettings.notSet:Not set`;
  private readonly time =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('time');
  private readonly author =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('author');
  private readonly actions =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('actions');
  protected readonly rowId = (row: AiSettingsVersionSummary): number => row.version_no;
  protected readonly rows = computed(() =>
    (this.store.historyPage()?.items ?? []).filter((row) =>
      (row.reason ?? '').toLocaleLowerCase().includes(this.filter().toLocaleLowerCase()),
    ),
  );
  protected readonly historyError = computed(() => {
    const error = this.store.history.error();
    return isApiRequestError(error) ? error.error : null;
  });
  protected readonly detailError = computed(() => {
    const error = this.store.detail.error();
    return isApiRequestError(error) ? error.error : null;
  });
  protected readonly comparisonError = computed(() => {
    const error = this.store.comparison.error();
    return isApiRequestError(error) ? error.error : null;
  });
  protected readonly columns = computed<readonly DataTableColumn<AiSettingsVersionSummary>[]>(
    () => [
      {
        id: 'version',
        header: $localize`:@@authoring.aiSettings.versionColumn:Version`,
        cell: (row) => row.version_no,
        role: 'number',
        align: 'end',
      },
      {
        id: 'reason',
        header: $localize`:@@authoring.aiSettings.reasonColumn:Reason`,
        cell: (row) => row.reason ?? $localize`:@@authoring.aiSettings.notSet:Not set`,
        role: 'text',
        priority: 'primary',
      },
      {
        id: 'author',
        header: this.authorLabel,
        cell: (row) => row.author_membership_id,
        role: 'identity',
        ...(this.author() ? { cellTemplate: this.author()! } : {}),
      },
      {
        id: 'time',
        header: $localize`:@@authoring.aiSettings.time:Saved at (UTC)`,
        cell: (row) => row.created_at,
        role: 'date',
        ...(this.time() ? { cellTemplate: this.time()! } : {}),
      },
      {
        id: 'actions',
        header: $localize`:@@authoring.aiSettings.actions:Inspection`,
        cell: () => '',
        role: 'actions',
        ...(this.actions() ? { cellTemplate: this.actions()! } : {}),
      },
    ],
  );
}
