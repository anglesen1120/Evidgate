import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  linkedSignal,
  output,
  signal,
  viewChild,
  type TemplateRef,
} from '@angular/core';
import type { AiSettingsVersionSummary } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmCard, HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import {
  DataTableComponent,
  type DataTableColumn,
  type DataTableCellContext,
} from '../../../shared/ui/data-table';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { isApiRequestError } from '../../../core/http/api-error';
import { AiSettingsStore } from './ai-settings.store';
import { FIELDS, textOf } from './ai-settings.model';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { AiSettingsSnapshotFieldsComponent } from './ai-settings-snapshot-fields.component';

@Component({
  selector: 'app-ai-settings-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmDialogImports,
    HlmIcon,
    AiSettingsSnapshotFieldsComponent,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HlmCardTitle,
    HlmInput,
    HlmLabel,
    DataTableComponent,
    EmptyPatternComponent,
    RequestStateComponent,
    AbsoluteTimeComponent,
    CopyableIdentifierComponent,
  ],
  templateUrl: './ai-settings-history.component.html',
})
export class AiSettingsHistoryComponent {
  protected readonly store = inject(AiSettingsStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly openSettings = output<void>();
  protected readonly escape = escapeFormatChars;
  protected readonly filter = signal('');
  protected readonly fields = FIELDS;
  protected readonly textOf = textOf;
  protected readonly authorLabel = $localize`:@@authoring.ai-settings.author:Author membership`;
  protected readonly settingsIdLabel = $localize`:@@authoring.ai-settings.settings-id:Settings identity`;
  protected readonly hashLabel = $localize`:@@authoring.ai-settings.hash:Content hash`;
  protected readonly tableCaption = $localize`:@@authoring.ai-settings.saved-versions:Saved settings versions`;
  protected readonly pagesLabel = $localize`:@@authoring.ai-settings.pages:Version history pages`;
  protected readonly noMatch = $localize`:@@authoring.ai-settings.no-match:No loaded versions match this filter`;
  protected readonly empty = $localize`:@@authoring.ai-settings.empty-history:No saved settings versions`;
  protected readonly emptyPage = $localize`:@@authoring.ai-settings.empty-page:No versions on this page`;
  protected readonly firstVersionAction = $localize`:@@authoring.ai-settings.open-settings:Open AI settings`;
  protected readonly firstVersionHelp = $localize`:@@authoring.ai-settings.first-version-help:Save your settings to create the first version.`;
  protected readonly notSet = $localize`:@@authoring.ai-settings.not-set:Not set`;
  private readonly time =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('time');
  private readonly author =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('author');
  private readonly actions =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('actions');
  private readonly comparisonActions =
    viewChild<TemplateRef<DataTableCellContext<AiSettingsVersionSummary>>>('compare');
  protected readonly viewedNo = linkedSignal({
    source: this.store.scope,
    computation: (): number | null => null,
  });
  private opener: HTMLButtonElement | null = null;
  protected readonly rowId = (row: AiSettingsVersionSummary): number => row.version_no;
  protected readonly rows = computed(() =>
    (this.store.historyPage()?.items ?? []).filter((row) =>
      (row.reason ?? '').toLocaleLowerCase().includes(this.filter().toLocaleLowerCase()),
    ),
  );
  protected readonly viewed = computed(() =>
    this.rows().find((row) => row.version_no === this.viewedNo()),
  );
  protected readonly loadedDetail = computed(() => {
    const row = this.viewed();
    if (!row) return undefined;
    const first = this.store.versionDetail();
    if (first?.version_no === row.version_no && first.content_hash === row.content_hash)
      return first;
    const second = this.store.comparedVersion();
    return second?.version_no === row.version_no && second.content_hash === row.content_hash
      ? second
      : undefined;
  });
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
        header: $localize`:@@authoring.ai-settings.version-column:Version`,
        cell: (row) => row.version_no,
        role: 'number',
        align: 'end',
      },
      {
        id: 'reason',
        header: $localize`:@@authoring.ai-settings.reason-column:Reason`,
        cell: (row) => row.reason ?? $localize`:@@authoring.ai-settings.not-set:Not set`,
        role: 'text',
        priority: 'primary',
      },
      {
        id: 'author',
        header: this.authorLabel,
        cell: (row) => row.author_membership_id,
        role: 'identity',
        ...this.optionalTemplate(this.author()),
      },
      {
        id: 'time',
        header: $localize`:@@authoring.ai-settings.time:Saved at (UTC)`,
        cell: (row) => row.created_at,
        role: 'date',
        ...this.optionalTemplate(this.time()),
      },
      {
        id: 'comparison',
        header: $localize`:@@authoring.ai-settings.comparison:Comparison`,
        cell: () => '',
        role: 'text',
        ...this.optionalTemplate(this.comparisonActions()),
      },
      {
        id: 'actions',
        header: $localize`:@@authoring.ai-settings.actions:Inspection`,
        cell: () => '',
        role: 'actions',
        ...this.optionalTemplate(this.actions()),
      },
    ],
  );

  private optionalTemplate(
    cellTemplate: TemplateRef<DataTableCellContext<AiSettingsVersionSummary>> | undefined,
  ) {
    return cellTemplate ? { cellTemplate } : {};
  }
  protected viewName(version: number): string {
    return $localize`:@@authoring.ai-settings.view-version:View version ${version}:VERSION:`;
  }
  protected inspect(version: number, opener: HTMLButtonElement): void {
    this.opener = opener;
    this.viewedNo.set(version);
  }
  protected restoreFocus(): void {
    const opener = this.opener;
    const root = this.host.nativeElement;
    const same = opener?.getAttribute('data-ai-version-view');
    const target = opener?.isConnected
      ? opener
      : (same && root.querySelector<HTMLElement>(`[data-ai-version-view="${Number(same)}"]`)) ||
        root.querySelector<HTMLElement>('[data-ai-version-view]') ||
        root.querySelector<HTMLElement>('[data-ai-history-heading]');
    target?.focus();
    this.opener = null;
  }
}
