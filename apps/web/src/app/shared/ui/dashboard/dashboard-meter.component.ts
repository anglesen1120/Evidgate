import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-dashboard-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-3)' },
  template: `
    <p class="m-0 font-medium">{{ label() }}</p>
    @if (ratio(); as current) {
      <meter
        class="h-(--eg-space-4) w-full accent-primary"
        [attr.aria-label]="label()"
        [min]="0"
        [max]="current.maximum"
        [value]="current.value"
      ></meter>
    }
    <p class="m-0 text-sm text-muted-foreground">{{ description() }}</p>
    <ng-content />
  `,
})
export class DashboardMeterComponent {
  readonly label = input.required<string>();
  readonly value = input<number | null>(null);
  readonly maximum = input<number | null>(null);
  readonly description = input('');
  protected readonly ratio = computed(() => {
    const maximum = this.maximum();
    const value = this.value();
    if (
      maximum === null ||
      maximum <= 0 ||
      value === null ||
      !Number.isFinite(maximum) ||
      !Number.isFinite(value)
    )
      return null;
    return { maximum, value: Math.max(0, Math.min(value, maximum)) };
  });
}
