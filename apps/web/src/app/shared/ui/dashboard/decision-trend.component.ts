import { FocusKeyManager, type FocusableOption } from '@angular/cdk/a11y';
import { Directionality } from '@angular/cdk/bidi';
import { DecimalPipe, formatNumber } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  LOCALE_ID,
  Renderer2,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTableImports } from '@evidgate/ui/table';
import { openFullValuePopover } from '@evidgate/ui/utils';
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
  imports: [DecimalPipe, HlmButton, HlmTableImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <figure class="m-0 grid min-w-0 gap-(--eg-space-4)" [attr.aria-label]="label()">
      <div
        #plot
        class="trend-bars focus-visible:outline-solid focus-visible:outline-ring"
        role="region"
        tabindex="0"
        [attr.aria-label]="label()"
      >
        <div class="trend-days" role="toolbar" [attr.aria-label]="daysLabel">
          @for (point of bars(); track point.id) {
            <div class="trend-column">
              <div class="trend-stack">
                <button
                  #bar
                  hlmBtn
                  type="button"
                  [variant]="'ghost'"
                  class="trend-trigger"
                  [tabIndex]="tabStop() === point.id ? 0 : -1"
                  [attr.aria-label]="point.summary"
                  (focus)="focusDay(point.id, bar, point.summary)"
                  (blur)="blurDay(bar)"
                  (pointerenter)="hoverDay($event, bar, point.summary)"
                  (pointerleave)="leave($event, bar)"
                  (pointerdown)="beginTap($event, bar)"
                  (pointercancel)="closingTap = null"
                  (click)="activate(bar, point.summary)"
                  (keydown)="navigate($event, $index)"
                >
                  <span class="trend-segments" aria-hidden="true" [style.height.%]="point.height">
                    @for (segment of point.segments; track segment.id) {
                      <span
                        data-segment
                        aria-hidden="true"
                        [attr.data-tone]="segment.tone"
                        [attr.data-pattern]="segment.pattern"
                        [style.flex-grow]="segment.value"
                      ></span>
                    }
                    <span class="trend-total text-xs font-medium tabular-nums">
                      {{ point.total | number }}
                    </span>
                  </span>
                </button>
              </div>
              <span aria-hidden="true" class="text-xs text-muted-foreground">{{
                point.label
              }}</span>
            </div>
          }
        </div>
      </div>
      <span
        #panel
        popover="manual"
        class="spartan-tooltip-content overflow-auto whitespace-pre-line tabular-nums"
        [attr.role]="inspection() ? 'tooltip' : null"
        (pointerleave)="leave($event)"
        >{{ inspection()?.summary }}</span
      >
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
                  <th hlmTh>{{ dateLabel }}</th>
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
  styleUrl: '../../../ui/utils/lib/full-value-popover.css',
  styles: `
    .trend-bars {
      padding-inline: calc(var(--eg-focus-width) + var(--eg-focus-offset));
      min-inline-size: 0;
      overflow-x: auto;
      padding-block: calc(var(--eg-leading-xs) + var(--eg-space-1)) var(--eg-space-1);
    }
    .trend-days {
      display: flex;
      inline-size: max-content;
      min-inline-size: 100%;
      align-items: flex-start;
      gap: var(--eg-space-4);
    }
    .trend-column {
      flex: 1 0 4.5rem;
      min-inline-size: 4.5rem;
      text-align: center;
      overflow-wrap: anywhere;
    }
    .trend-stack {
      position: relative;
      block-size: 10rem;
      border-block-end: var(--eg-border-width) solid var(--eg-border-control);
    }
    .trend-trigger {
      display: block;
      position: relative;
      block-size: 100%;
      inline-size: 4.5rem;
      margin-inline: auto;
      border: 0;
      padding: 0;
      cursor: help;
    }
    .trend-trigger:focus-visible {
      outline: var(--eg-focus-width) solid var(--eg-focus);
      outline-offset: var(--eg-focus-offset);
    }
    .trend-segments {
      position: absolute;
      inset-block-end: 0;
      inset-inline: 0;
      max-inline-size: 4.5rem;
      margin-inline: auto;
      display: flex;
      flex-direction: column-reverse;
    }
    .trend-segments [data-segment] {
      flex-basis: 0;
    }
    .trend-total {
      position: absolute;
      inset-block-end: 100%;
      inset-inline: 0;
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
  private readonly locale = inject(LOCALE_ID);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly renderer = inject(Renderer2);
  private readonly directionality = inject(Directionality);
  readonly label = input.required<string>();
  readonly points = input<readonly DashboardTrendPoint[]>([]);
  protected readonly daysLabel = $localize`:@@shared.dashboard.trend-days:Daily decision counts`;
  protected readonly dateLabel = $localize`:@@shared.dashboard.trend-date:Date (UTC)`;
  private readonly totalLabel = $localize`:@@shared.dashboard.trend-total:Total`;
  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');
  private readonly plot = viewChild.required<ElementRef<HTMLElement>>('plot');
  private readonly buttons = viewChildren<ElementRef<HTMLButtonElement>>('bar');
  private readonly navigation = new FocusKeyManager<FocusableOption>(
    computed(() =>
      this.buttons().map(({ nativeElement }) => ({ focus: () => nativeElement.focus() })),
    ),
    this.injector,
  )
    .withVerticalOrientation(false)
    .withHomeAndEnd();
  protected closingTap: HTMLButtonElement | null = null;
  private readonly focusedDay = signal<string | null>(null);
  protected readonly tabStop = computed(() => {
    const focused = this.focusedDay();
    return this.points().some((day) => day.id === focused) ? focused : this.points()[0]?.id;
  });
  // Never keep a stale day's readout open after the dashboard replaces its sample.
  protected readonly inspection = linkedSignal<
    readonly DashboardTrendPoint[],
    { trigger: HTMLButtonElement; summary: string } | null
  >({ source: this.points, computation: () => null });
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
    const totals = this.points().map((point) => ({
      point,
      total: point.segments.reduce((sum, segment) => sum + Math.max(0, segment.value), 0),
    }));
    const maximum = totals.reduce((max, { total }) => Math.max(max, total), 1);
    return totals.map(({ point, total }) => ({
      ...point,
      total,
      summary: [
        `${this.dateLabel}: ${point.label}`,
        ...point.segments.map(
          ({ label, value }) => `${label}: ${formatNumber(value, this.locale)}`,
        ),
        `${this.totalLabel}: ${formatNumber(total, this.locale)}`,
      ].join('\n'),
      height: (total / maximum) * 100,
      segments: point.segments
        .map((segment, index) => ({
          ...segment,
          pattern: index % 4,
        }))
        .filter((segment) => segment.value > 0),
    }));
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.navigation.destroy());
    // The tooltip primitive ignores touch. Use the same top-layer anchor as full table values.
    afterRenderEffect((onCleanup) => {
      const current = this.inspection();
      if (!current) return;
      const panel = this.panel().nativeElement;
      const dismiss = () => {
        if (this.inspection() === current) this.inspection.set(null);
      };
      const close = openFullValuePopover(current.trigger, panel, dismiss, {
        above: current.trigger.querySelector('.trend-segments') ?? current.trigger,
        within: this.plot().nativeElement,
      });
      const unlisten = this.renderer.listen(
        'document',
        'pointerdown',
        (event: PointerEvent) => {
          if (!this.inside(event.target, current.trigger, panel)) dismiss();
        },
        { capture: true },
      );
      onCleanup(() => {
        unlisten();
        close();
      });
    });
  }

  protected beginTap(event: PointerEvent, trigger: HTMLButtonElement): void {
    // Touch can focus the button before click. Toggle from the state at finger-down.
    this.closingTap =
      event.pointerType === 'touch' && this.inspection()?.trigger === trigger ? trigger : null;
  }

  protected activate(trigger: HTMLButtonElement, summary: string): void {
    if (this.closingTap === trigger) this.blurDay(trigger);
    else this.inspect(trigger, summary);
    this.closingTap = null;
  }

  private inspect(trigger: HTMLButtonElement, summary: string): void {
    if (this.inspection()?.trigger !== trigger) this.inspection.set({ trigger, summary });
  }

  protected focusDay(id: string, trigger: HTMLButtonElement, summary: string): void {
    this.focusedDay.set(id);
    this.inspect(trigger, summary);
  }

  protected blurDay(trigger: HTMLButtonElement): void {
    if (this.inspection()?.trigger === trigger) this.inspection.set(null);
  }

  protected hoverDay(event: PointerEvent, trigger: HTMLButtonElement, summary: string): void {
    if (event.pointerType === 'mouse' || event.pointerType === 'pen')
      this.inspect(trigger, summary);
  }

  protected leave(event: PointerEvent, from?: HTMLButtonElement): void {
    const current = this.inspection();
    if (!current || event.pointerType === 'touch' || (from && from !== current.trigger)) return;
    if (current.trigger.ownerDocument.activeElement === current.trigger) return;
    if (!this.inside(event.relatedTarget, current.trigger, this.panel().nativeElement)) {
      this.inspection.set(null);
    }
  }

  protected navigate(event: KeyboardEvent, index: number): void {
    if (event.key === 'Escape' && this.inspection()) {
      event.stopPropagation();
      this.inspection.set(null);
      return;
    }
    this.navigation.updateActiveItem(index);
    this.navigation.withHorizontalOrientation(this.directionality.value).onKeydown(event);
  }

  private inside(target: EventTarget | null, trigger: HTMLElement, panel: HTMLElement): boolean {
    return target instanceof Node && (trigger.contains(target) || panel.contains(target));
  }
}
