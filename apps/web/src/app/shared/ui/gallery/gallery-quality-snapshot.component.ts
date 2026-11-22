import { ChangeDetectionStrategy, Component, inject, LOCALE_ID } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';
import { HlmTooltip } from '@evidgate/ui/tooltip';

const series = [
  { key: 'pass', label: $localize`:@@shared.gallery.quality.pass:Pass` },
  { key: 'fail', label: $localize`:@@shared.gallery.quality.fail:Fail` },
  { key: 'other', label: $localize`:@@shared.gallery.quality.other:Other · unspecified` },
] as const;
const days = [
  { date: '2026-10-11', pass: 16, fail: 3, other: 1 },
  { date: '2026-10-12', pass: 18, fail: 2, other: 0 },
] as const;

const counts = days.reduce(
  (total, day) => ({
    pass: total.pass + day.pass,
    fail: total.fail + day.fail,
    other: total.other + day.other,
  }),
  { pass: 0, fail: 0, other: 0 },
);

@Component({
  selector: 'app-gallery-quality-snapshot',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardImports, HlmTableImports, HlmTooltip],
  host: { class: 'block min-w-0' },
  templateUrl: './gallery-quality-snapshot.component.html',
  styleUrl: './gallery-quality-snapshot.component.css',
})
export class GalleryQualitySnapshotComponent {
  private readonly locale = inject(LOCALE_ID);
  private readonly percent = new Intl.NumberFormat(this.locale, {
    style: 'percent',
    maximumFractionDigits: 1,
  });
  private readonly date = new Intl.DateTimeFormat(this.locale, {
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  });
  protected readonly keys = series;
  protected readonly days = days.map((day) => {
    const label = this.date.format(new Date(day.date));
    const values = series.map(({ key, label: outcome }) => `${outcome}: ${day[key]}`).join(' · ');
    return {
      ...day,
      label,
      summary: `${label} · ${values}`,
      total: day.pass + day.fail + day.other,
    };
  });
  protected readonly scale = Math.max(...this.days.map((day) => day.total));
  protected readonly total = counts.pass + counts.fail + counts.other;
  protected readonly breakdown = series.map(({ key, label }) => ({
    key,
    label,
    count: counts[key],
    share: this.percent.format(counts[key] / this.total),
  }));
  protected readonly passStop = `${(100 * counts.pass) / this.total}%`;
  protected readonly decidedStop = `${(100 * (counts.pass + counts.fail)) / this.total}%`;
  protected readonly failureRate = this.percent.format(
    days[1].fail / (days[1].pass + days[1].fail + days[1].other),
  );
  protected readonly ranked = [
    {
      label: $localize`:@@shared.gallery.quality.postcode:Address formats postcode`,
      runs: 24,
      rate: 12.5,
    },
    {
      label: $localize`:@@shared.gallery.quality.search:Search products by phrase`,
      runs: 36,
      rate: 8.3,
    },
  ].map((item) => ({ ...item, displayRate: this.percent.format(item.rate / 100) }));
  protected readonly reasons = [
    $localize`:@@shared.gallery.quality.defect:Product defect`,
    $localize`:@@shared.gallery.quality.drift:Test drift`,
  ];
  protected readonly runId = 'run-1842';
}
