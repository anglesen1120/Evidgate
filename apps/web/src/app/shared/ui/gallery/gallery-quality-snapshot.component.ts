import { formatDate } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, LOCALE_ID, signal } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmTableImports } from '@evidgate/ui/table';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { shortDateFormat } from '../../time/short-date-format';

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
  imports: [HlmCardImports, HlmTableImports, HlmTooltip, HlmIcon],
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
  private readonly dateFormat = shortDateFormat(this.locale);
  protected readonly keys = series;
  protected readonly days = [this.describeDay(days[0]), this.describeDay(days[1])] as const;
  protected readonly compare = signal(true);
  protected readonly period = $localize`:@@shared.gallery.quality.period:${this.days[0].label}:first:–${this.days[1].label}:last: · UTC`;
  protected readonly rateTitle = $localize`:@@shared.gallery.quality.rate-title:Failure rate · ${this.days[1].label}:date:`;
  protected readonly comparison = $localize`:@@shared.gallery.quality.comparison:Compared with 3 of 20 on ${this.days[0].label}:date:.`;

  private describeDay(day: (typeof days)[number]) {
    const label = formatDate(day.date, this.dateFormat, this.locale);
    const values = series.map(({ key, label: outcome }) => `${outcome}: ${day[key]}`).join(' · ');
    return {
      ...day,
      label,
      summary: `${label} · ${values}`,
      total: day.pass + day.fail + day.other,
    };
  }
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
