import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon, type HlmIconName } from '@evidgate/ui/icon';
import { BADGE_TONE_CLASSES } from '../chips/badge-classes';
import { NumberValueComponent } from '../values/number-value.component';

export type DashboardTone = 'neutral' | 'success' | 'danger' | 'warning' | 'info';

@Component({
  selector: 'app-dashboard-kpi',
  imports: [HlmCard, HlmIcon, NumberValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="h-full min-w-0 gap-(--eg-space-2) p-(--eg-space-5)"
      [attr.aria-label]="label()"
      [attr.data-tone]="tone()"
    >
      <h3
        class="m-0 flex items-center gap-(--eg-space-2) text-sm font-medium text-muted-foreground"
      >
        @if (icon(); as name) {
          <hlm-icon
            class="size-(--eg-space-7) rounded-(--eg-radius-sm) p-(--eg-space-1)"
            [name]="name"
            [class]="iconTone()"
          />
        }
        <span class="min-w-0 wrap-anywhere">{{ label() }}</span>
      </h3>
      <p class="m-0 text-3xl font-semibold tabular-nums wrap-anywhere">
        @if (numericValue(); as current) {
          <app-number-value [value]="current" />
        } @else if (value() === 0) {
          <app-number-value [value]="0" />
        } @else {
          {{ value() ?? unavailableLabel() }}
        }
      </p>
      @if (detail()) {
        <p class="m-0 text-xs text-muted-foreground">{{ detail() }}</p>
      }
    </section>
  `,
})
export class DashboardKpiComponent {
  readonly label = input.required<string>();
  readonly value = input<string | number | null>(null);
  readonly detail = input('');
  readonly unavailableLabel = input('');
  readonly tone = input<DashboardTone>('neutral');
  readonly icon = input<HlmIconName | null>(null);
  protected readonly iconTone = computed(() => BADGE_TONE_CLASSES[this.tone()]);
  protected readonly numericValue = computed(() =>
    typeof this.value() === 'number' ? this.value() : null,
  );
}
