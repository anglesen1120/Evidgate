import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
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
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <figure class="m-0 grid min-w-0 gap-(--eg-space-4)" [attr.aria-label]="label()">
      <div class="trend-bars" aria-hidden="true">
        @for (point of bars(); track point.id) {
          <div class="trend-column">
            <div class="trend-stack">
              @for (segment of point.segments; track segment.id) {
                <span
                  data-segment
                  aria-hidden="true"
                  [attr.data-tone]="segment.tone"
                  [style.height.%]="segment.height"
                ></span>
              }
            </div>
            <span class="text-xs text-muted-foreground">{{ point.label }}</span>
          </div>
        }
      </div>
      <figcaption>
        <ul class="m-0 grid list-none gap-(--eg-space-2) p-0 text-xs text-muted-foreground">
          @for (point of points(); track point.id) {
            <li>
              <strong class="font-medium text-foreground">{{ point.label }}</strong>
              @for (segment of point.segments; track segment.id) {
                <span class="ms-(--eg-space-3)"
                  >{{ segment.label }}: {{ segment.value | number }}</span
                >
              }
            </li>
          }
        </ul>
      </figcaption>
    </figure>
  `,
  styles: `
    .trend-bars {
      display: flex;
      align-items: end;
      gap: var(--eg-space-4);
      min-inline-size: 0;
    }
    .trend-column {
      flex: 1;
      min-inline-size: 0;
      text-align: center;
      overflow-wrap: anywhere;
    }
    .trend-stack {
      display: flex;
      flex-direction: column-reverse;
      justify-content: start;
      block-size: 10rem;
      gap: var(--eg-space-1);
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
  `,
})
export class DecisionTrendComponent {
  readonly label = input.required<string>();
  readonly points = input<readonly DashboardTrendPoint[]>([]);
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
      segments: point.segments.map((segment) => ({
        ...segment,
        height: (Math.max(0, segment.value) / maximum) * 100,
      })),
    }));
  });
}
