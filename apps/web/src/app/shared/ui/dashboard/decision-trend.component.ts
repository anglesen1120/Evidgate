import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { HlmTableImports } from '@evidgate/ui/table';
import type { DashboardTone } from './dashboard-kpi.component';

export interface DashboardTrendSegment {
  readonly id: string;
  readonly label: string;
  readonly value: number;
  readonly tone: DashboardTone;
}
export interface DashboardTrendPoint {
  readonly id: string;
  readonly label: string;
  readonly segments: readonly DashboardTrendSegment[];
}

@Component({
  selector: 'app-decision-trend',
  imports: [DecimalPipe, HlmTableImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <figure class="m-0 grid min-w-0 gap-(--eg-space-4)" [attr.aria-label]="label()">
      <div
        class="trend-bars focus-visible:outline-solid focus-visible:outline-ring"
        role="region"
        tabindex="0"
        [attr.aria-label]="label()"
      >
        @for (point of bars(); track point.id) {
          <div class="trend-column" aria-hidden="true">
            <div class="trend-stack">
              @for (segment of point.segments; track segment.id) {
                <span
                  data-segment
                  aria-hidden="true"
                  [attr.data-tone]="segment.tone"
                  [attr.data-pattern]="segment.pattern"
                  [style.height.%]="segment.height"
                ></span>
              }
            </div>
            <span class="text-xs text-muted-foreground">{{ point.label }}</span>
          </div>
        }
      </div>
      <figcaption>
        <ul class="m-0 mb-(--eg-space-4) flex list-none flex-wrap gap-(--eg-space-4) p-0 text-xs">
          @for (segment of legend(); track segment.id) {
            <li class="flex items-center gap-(--eg-space-2)">
              <span
                data-segment
                aria-hidden="true"
                class="size-(--eg-space-3)"
                [attr.data-tone]="segment.tone"
                [attr.data-pattern]="$index % 4"
              ></span>
              <span>{{ segment.label }}: {{ segment.total | number }}</span>
            </li>
          }
        </ul>
        <details>
          <summary
            class="min-h-(--eg-control-touch) cursor-pointer text-sm font-medium text-primary focus-visible:outline-solid focus-visible:outline-ring"
            i18n="@@shared.dashboard.trend-table"
          >
            View as table
          </summary>
          <div hlmTableContainer>
            <table hlmTable>
              <caption class="sr-only">
                {{
                  label()
                }}
              </caption>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh i18n="@@shared.dashboard.trend-date">Date (UTC)</th>
                  @for (segment of points()[0]?.segments ?? []; track segment.id) {
                    <th hlmTh>{{ segment.label }}</th>
                  }
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (point of points(); track point.id) {
                  <tr hlmTr>
                    <th hlmTh [attr.scope]="'row'">{{ point.label }}</th>
                    @for (segment of point.segments; track segment.id) {
                      <td hlmTd class="tabular-nums">{{ segment.value | number }}</td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </details>
      </figcaption>
    </figure>
  `,
  styles: `
    .trend-bars {
      display: flex;
      align-items: end;
      gap: var(--eg-space-4);
      min-inline-size: 0;
      overflow-x: auto;
      padding-block: var(--eg-space-1);
    }
    .trend-column {
      flex: 1 0 4.5rem;
      min-inline-size: 4.5rem;
      text-align: center;
      overflow-wrap: anywhere;
    }
    .trend-stack {
      display: flex;
      flex-direction: column-reverse;
      justify-content: start;
      block-size: 10rem;
    }
    [data-segment] {
      display: block;
      background: var(--eg-neutral-fg);
      border-radius: var(--eg-radius-sm);
    }
    [data-tone='success'] {
      background: var(--eg-success-fg);
    }
    [data-tone='danger'] {
      background: var(--eg-danger-fg);
    }
    [data-tone='warning'] {
      background: var(--eg-warning-fg);
    }
    [data-tone='info'] {
      background: var(--eg-info-fg);
    }
    [data-pattern='1'] {
      background-image: repeating-linear-gradient(
        90deg,
        transparent,
        transparent var(--eg-space-2),
        var(--eg-surface) var(--eg-space-2),
        var(--eg-surface) var(--eg-space-3)
      );
    }
    [data-pattern='2'] {
      background-image: repeating-linear-gradient(
        135deg,
        transparent,
        transparent var(--eg-space-2),
        var(--eg-surface) var(--eg-space-2),
        var(--eg-surface) var(--eg-space-3)
      );
    }
    [data-pattern='3'] {
      background-image: repeating-linear-gradient(
        0deg,
        transparent,
        transparent var(--eg-space-2),
        var(--eg-surface) var(--eg-space-2),
        var(--eg-surface) var(--eg-space-3)
      );
    }
  `,
})
export class DecisionTrendComponent {
  readonly label = input.required<string>();
  readonly points = input<readonly DashboardTrendPoint[]>([]);
  protected readonly legend = computed(() =>
    (this.points()[0]?.segments ?? []).map((segment) => ({
      ...segment,
      total: this.points().reduce(
        (sum, point) =>
          sum + Math.max(0, point.segments.find((item) => item.id === segment.id)?.value ?? 0),
        0,
      ),
    })),
  );
  protected readonly bars = computed(() => {
    const points = this.points();
    const maximum = Math.max(
      1,
      ...points.map((point) =>
        point.segments.reduce((sum, segment) => sum + Math.max(0, segment.value), 0),
      ),
    );
    return points.map((point) => ({
      ...point,
      segments: point.segments
        .map((segment, index) => ({
          ...segment,
          pattern: index % 4,
          height: (Math.max(0, segment.value) / maximum) * 100,
        }))
        .filter((segment) => segment.value > 0),
    }));
  });
}
