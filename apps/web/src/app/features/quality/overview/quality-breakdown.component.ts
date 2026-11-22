import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { DataTableComponent, type DataTableColumn } from '../../../shared/ui/data-table';
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
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-4 wrap-anywhere' },
  templateUrl: './quality-breakdown.component.html',
})
export class QualityBreakdownComponent {
  readonly model = input.required<QualityOverviewViewModel>();
  readonly longNames = input(false);
  protected readonly rowId = (row: QualityCaseViewModel) => row.id;
  protected readonly copy = {
    flaky: $localize`:@@review.quality.flaky:Most flaky cases`,
    limit: $localize`:@@review.quality.flake-limit:2 cases shown · server limit 50. Flake rate is not a pass rate or a person-level ranking.`,
    bindings: $localize`:@@review.quality.bindings:Suite and component evidence`,
    scope: $localize`:@@review.quality.binding-scope:Frozen references, not aggregate pass rates. Release components are pinned to rel-2026.10.13 / rd-14.`,
    unbound: $localize`:@@review.quality.unbound:No eligible run`,
  };
  protected readonly columns = computed<readonly DataTableColumn<QualityCaseViewModel>[]>(() => {
    const long = this.longNames();
    return [
      {
        id: 'case',
        header: $localize`:@@review.quality.case:Case`,
        role: 'identity',
        overflow: 'wrap',
        cell: (row) => `${(long && row.longName) || row.name} · ${row.id}`,
      },
      {
        id: 'rate',
        header: $localize`:@@review.quality.flake-rate:Flake rate`,
        role: 'number',
        align: 'end',
        cell: (row) => `${row.flakePercent}%`,
      },
      {
        id: 'runs',
        header: $localize`:@@review.quality.observed:Observed runs`,
        role: 'number',
        align: 'end',
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
