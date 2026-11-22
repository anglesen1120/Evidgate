import { ChangeDetectionStrategy, Component, computed, input, inject } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { DataTableComponent, type DataTableColumn } from '../../../shared/ui/data-table';
import type { QualityDayViewModel } from './quality-overview.fixture';

const ChartDisplay = signalStore(
  withState({ tableVisible: false }),
  withMethods((store) => ({
    toggle(): void {
      patchState(store, { tableVisible: !store.tableVisible() });
    },
  })),
);

const noData = $localize`:@@review.quality.no-data:No data`;
const columns: readonly DataTableColumn<QualityDayViewModel>[] = [
  {
    id: 'date',
    header: $localize`:@@review.quality.date:Date · UTC`,
    role: 'identity',
    cell: (day) => day.date,
  },
  {
    id: 'passed',
    header: $localize`:@@review.quality.passed:Passed`,
    role: 'number',
    align: 'end',
    cell: (day) => day.observation?.passed ?? noData,
  },
  {
    id: 'failed',
    header: $localize`:@@review.quality.failed:Failed`,
    role: 'number',
    align: 'end',
    cell: (day) => day.observation?.failed ?? noData,
  },
  {
    id: 'other',
    header: $localize`:@@review.quality.other:Other`,
    role: 'number',
    align: 'end',
    cell: (day) => day.observation?.other ?? noData,
  },
  {
    id: 'complete',
    header: $localize`:@@review.quality.complete:Complete runs`,
    role: 'number',
    align: 'end',
    cell: (day) => day.observation?.complete ?? noData,
  },
  {
    id: 'rate',
    header: $localize`:@@review.quality.pass-rate:Pass rate`,
    role: 'number',
    align: 'end',
    cell: (day) => (day.observation ? `${day.observation.percent}%` : noData),
  },
];

@Component({
  selector: 'app-quality-history',
  imports: [DataTableComponent, HlmButton, HlmCardImports],
  providers: [ChartDisplay],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-4 wrap-anywhere' },
  templateUrl: './quality-history.component.html',
  styleUrl: './quality-history.component.css',
})
export class QualityHistoryComponent {
  protected readonly display = inject(ChartDisplay);
  readonly days = input.required<readonly QualityDayViewModel[]>();
  protected readonly columns = columns;
  protected readonly rowId = (day: QualityDayViewModel) => day.date;
  protected readonly copy = {
    title: $localize`:@@review.quality.trend:Pass rate of complete runs`,
    table: $localize`:@@review.quality.as-table:View as table`,
    chart: $localize`:@@review.quality.as-chart:View as chart`,
    noData,
    description: $localize`:@@review.quality.exclusions:Daily observations · imported, canceled and incomplete runs excluded.`,
    gap: $localize`:@@review.quality.gap:Only supplied days are shown. The gap on 10 October has no value; it is not a 0% observation.`,
    outcomes: $localize`:@@review.quality.outcomes:Complete-run outcomes`,
    counts: $localize`:@@review.quality.counts:Passed, failed and other · counts, not a release verdict.`,
  };
  protected readonly points = computed(() => {
    const days = this.days();
    return days.map((day, index) => ({
      day,
      x: ((index + 0.5) * 600) / days.length,
      y: day.observation === null ? null : (100 - day.observation.percent) * 3,
    }));
  });
  protected readonly segments = computed(() => {
    const points = this.points();
    return points.flatMap((point, index) => {
      const before = points[index - 1];
      if (!before || before.y === null || point.y === null) return [];
      return [
        {
          id: point.day.date,
          line: `${before.x},${before.y} ${point.x},${point.y}`,
          fill: `${before.x},150 ${before.x},${before.y} ${point.x},${point.y} ${point.x},150`,
        },
      ];
    });
  });
  protected readonly latestFirst = computed(() => [...this.days()].reverse());
  protected readonly scale = [100, 75, 50] as const;
}
