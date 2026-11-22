import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';

import { StatusPillComponent } from '../../../shared/ui/chips';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/ui/data-table';
import { SectionHeaderComponent } from '../../../shared/ui/page-header';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import { RELEASE_COPY } from './release-view-copy';
import type { ReleaseReviewState } from './release-view-state';
import {
  componentFilter,
  type ComponentFilter,
  type ComponentKey,
  type ReleaseBindingViewModel,
} from './release-view.fixture';

type Cell = TemplateRef<DataTableCellContext<ReleaseBindingViewModel>>;

@Component({
  selector: 'app-release-binding-matrix',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HlmLabel,
    HlmSelectImports,
    StatusPillComponent,
    DataTableComponent,
    SectionHeaderComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
  ],
  host: { class: 'block min-w-0 wrap-anywhere' },
  templateUrl: './release-binding-matrix.component.html',
})
export class ReleaseBindingMatrixComponent {
  readonly rows = input.required<readonly ReleaseBindingViewModel[]>();
  readonly filter = input<ComponentFilter>('all');
  readonly state = input.required<ReleaseReviewState>();
  readonly filterChanged = output<ComponentFilter>();
  readonly inspect = output<ComponentKey>();
  protected readonly copy = RELEASE_COPY;
  protected readonly rowId = (row: ReleaseBindingViewModel) => row.component;
  protected readonly filterValue = componentFilter;
  protected readonly filterLabel = (value: unknown) =>
    value === 'all' ? this.copy.all : componentFilter(value);
  protected readonly selectedRows = computed(() =>
    this.state() === 'no-match'
      ? []
      : this.filter() === 'all'
        ? this.rows()
        : this.rows().filter((row) => row.component === this.filter()),
  );

  protected unavailable(row: ReleaseBindingViewModel): boolean {
    return this.state() === 'partial' && row.component === 'worker';
  }

  private readonly componentCell = viewChild.required<Cell>('componentCell');
  private readonly digestCell = viewChild.required<Cell>('digestCell');
  private readonly bindingCell = viewChild.required<Cell>('bindingCell');
  private readonly freshnessCell = viewChild.required<Cell>('freshnessCell');
  private readonly evidenceCell = viewChild.required<Cell>('evidenceCell');
  private readonly outcomeCell = viewChild.required<Cell>('outcomeCell');
  private readonly decisionCell = viewChild.required<Cell>('decisionCell');
  protected readonly columns = computed<readonly DataTableColumn<ReleaseBindingViewModel>[]>(() => [
    {
      id: 'component',
      header: this.copy.component,
      role: 'identity',
      priority: 'primary',
      cell: (row) => row.component,
      cellTemplate: this.componentCell(),
    },
    {
      id: 'digest',
      header: this.copy.digest,
      role: 'text',
      priority: 'primary',
      cell: (row) => row.digest,
      cellTemplate: this.digestCell(),
    },
    {
      id: 'binding',
      header: this.copy.binding,
      role: 'text',
      priority: 'primary',
      cell: (row) => row.evaluation?.run ?? this.copy.unbound,
      cellTemplate: this.bindingCell(),
    },
    {
      id: 'freshness',
      header: this.copy.freshness,
      role: 'status',
      priority: 'primary',
      cell: (row) => row.evaluation?.freshness ?? this.copy.missing,
      cellTemplate: this.freshnessCell(),
    },
    {
      id: 'evidence',
      header: this.copy.evidence,
      role: 'status',
      priority: 'primary',
      cell: (row) => row.evaluation?.completeness ?? this.copy.noEvidence,
      cellTemplate: this.evidenceCell(),
    },
    {
      id: 'outcome',
      header: this.copy.outcome,
      role: 'status',
      priority: 'primary',
      cell: (row) => row.evaluation?.outcome ?? this.copy.unavailable,
      cellTemplate: this.outcomeCell(),
    },
    {
      id: 'decision',
      header: this.copy.decision,
      role: 'status',
      priority: 'primary',
      cell: (row) => row.evaluation?.decision ?? this.copy.unavailable,
      cellTemplate: this.decisionCell(),
    },
  ]);
}
