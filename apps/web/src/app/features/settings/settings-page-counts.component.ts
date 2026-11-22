import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard } from '@evidgate/ui/card';
import { NumberValueComponent } from '../../shared/ui/values/number-value.component';
import { ShellIconComponent } from '../../shell/layout/shell-icon.component';
import type { NavIconName } from '../../shell/nav/nav.models';

export interface SettingsPageCount {
  readonly label: string;
  readonly icon: NavIconName;
  readonly value: number;
}

@Component({
  selector: 'app-settings-page-counts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCard, NumberValueComponent, ShellIconComponent],
  host: { class: 'block min-w-0' },
  template: `
    <dl class="m-0 grid min-w-0 grid-cols-[repeat(auto-fit,minmax(min(100%,9rem),1fr))] gap-3">
      @for (metric of metrics(); track metric.label; let first = $first) {
        <div hlmCard class="min-w-0 gap-3 rounded-xl p-4">
          <dt class="flex min-w-0 items-center gap-2 font-semibold wrap-anywhere">
            <span
              class="grid size-7 shrink-0 place-items-center rounded-md"
              [class]="
                first
                  ? 'bg-(--eg-accent-soft) text-(--eg-accent)'
                  : 'bg-(--eg-neutral-bg) text-(--eg-neutral-fg)'
              "
            >
              <app-shell-icon [name]="metric.icon" />
            </span>
            {{ metric.label }}
          </dt>
          <dd class="m-0 grid gap-1">
            <app-number-value
              class="font-(family-name:--eg-font-display) text-2xl font-bold"
              [value]="metric.value"
            />
            <span class="text-xs text-muted-foreground" i18n="@@settings.count-on-page"
              >On this page</span
            >
          </dd>
        </div>
      }
    </dl>
  `,
})
export class SettingsPageCountsComponent {
  readonly metrics = input.required<readonly SettingsPageCount[]>();
}
