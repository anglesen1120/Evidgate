import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmTableImports } from '@evidgate/ui/table';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/ui/data-table';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import type { QualityCaseViewModel, QualityOverviewViewModel } from './quality-overview.fixture';

@Component({
  selector: 'app-quality-breakdown',
  imports: [
    DataTableComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
    HlmIcon,
    HlmCardImports,
    HlmTableImports,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-4 wrap-anywhere' },
  templateUrl: './quality-breakdown.component.html',
})
export class QualityBreakdownComponent {
  private readonly percent = new Intl.NumberFormat(inject(LOCALE_ID), {
    style: 'percent',
    maximumFractionDigits: 3,
  });
  readonly model = input.required<QualityOverviewViewModel>();
  readonly longNames = input(false);
  protected readonly rowId = (row: QualityCaseViewModel) => row.id;
  private readonly caseCell =
    viewChild.required<TemplateRef<DataTableCellContext<QualityCaseViewModel>>>('caseCell');
  private readonly rateCell =
    viewChild.required<TemplateRef<DataTableCellContext<QualityCaseViewModel>>>('rateCell');
  protected readonly copy = {
    flaky: $localize`:@@review.quality.flaky:Most flaky cases`,
    limit: (count: number) =>
      $localize`:@@review.quality.cases-shown:Showing ${count}:count: · server limit 50. Flake rate is not a pass rate or a person-level ranking.`,
    bindings: $localize`:@@review.quality.bindings:Suite and component evidence`,
    scope: $localize`:@@review.quality.binding-scope:Frozen references, not aggregate pass rates. Release components are pinned to rel-2026.10.13 / rd-14.`,
    unbound: $localize`:@@review.quality.unbound:No eligible run`,
    subject: $localize`:@@review.quality.bound-subject:Suite / component`,
    completeness: $localize`:@@review.decision.completeness:Collection completeness`,
    outcome: $localize`:@@review.decision.outcome:Test outcome`,
    decision: $localize`:@@review.decision.decision:Stored run decision`,
  };
  protected readonly columns = computed<readonly DataTableColumn<QualityCaseViewModel>[]>(() => {
    const long = this.longNames();
    return [
      {
        id: 'case',
        header: $localize`:@@review.quality.case:Case`,
        role: 'identity',
        overflow: 'wrap',
        cell: (row) => (long && row.longName) || row.name,
        cellTemplate: this.caseCell(),
      },
      {
        id: 'rate',
        header: $localize`:@@review.quality.flake-rate:Flake rate`,
        role: 'number',
        align: 'end',
        cell: (row) => this.percent.format(row.flakePercent / 100),
        width: { min: '12ch' },
        cellTemplate: this.rateCell(),
      },
      {
        id: 'runs',
        header: $localize`:@@review.quality.observed:Observed runs`,
        role: 'number',
        align: 'end',
        width: { min: 'calc(14ch + 2 * var(--eg-space-4))' },
        cell: (row) => row.runs,
      },
      {
        id: 'utc',
        header: $localize`:@@review.quality.last-flaked:Last flaked · UTC`,
        role: 'date',
        cell: (row) => row.lastFlaked ?? $localize`:@@review.quality.not-supplied:Not supplied`,
      },
    ];
  });
}
