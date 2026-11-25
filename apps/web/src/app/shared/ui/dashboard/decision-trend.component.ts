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
  viewChildren,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTableImports } from '@evidgate/ui/table';
import { EmptyPatternComponent } from '../states/empty-pattern.component';
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
  imports: [DecimalPipe, EmptyPatternComponent, HlmButton, HlmTableImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './decision-trend.component.html',
  styleUrl: './decision-trend.component.css',
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
  protected readonly emptyLabel = $localize`:@@shared.dashboard.trend-empty:No observed dates in this sample.`;
  protected readonly totalLabel = $localize`:@@shared.dashboard.trend-total:Total`;
  private readonly buttons = viewChildren<ElementRef<HTMLButtonElement>>('bar');
  private readonly navigation = new FocusKeyManager<FocusableOption>(
    computed(() =>
      this.buttons().map(({ nativeElement }) => ({ focus: () => nativeElement.focus() })),
    ),
    this.injector,
  )
    .withVerticalOrientation(true)
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
      ].join(' · '),
      width: (total / maximum) * 100,
      visibleSegments: point.segments
        .map((segment, index) => ({
          ...segment,
          pattern: index % 4,
        }))
        .filter((segment) => segment.value > 0),
    }));
  });
  protected readonly sampleSummary = computed(() => {
    const days = this.bars();
    const runs = formatNumber(
      days.reduce((sum, day) => sum + day.total, 0),
      this.locale,
    );
    const dates = formatNumber(days.length, this.locale);
    return $localize`:@@shared.dashboard.trend-sample:Sampled runs: ${runs}:runs: · Observed dates: ${dates}:dates:`;
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.navigation.destroy());
    afterRenderEffect((onCleanup) => {
      const current = this.inspection();
      if (!current) return;
      const dismiss = () => {
        if (this.inspection() === current) this.inspection.set(null);
      };
      const escape = this.renderer.listen('document', 'keydown.escape', dismiss);
      const unlisten = this.renderer.listen(
        'document',
        'pointerdown',
        (event: PointerEvent) => {
          if (!(event.target instanceof Node && current.trigger.contains(event.target))) dismiss();
        },
        { capture: true },
      );
      onCleanup(() => {
        unlisten();
        escape();
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

  protected leave(event: PointerEvent): void {
    const current = this.inspection();
    if (
      current &&
      event.pointerType !== 'touch' &&
      current.trigger.ownerDocument.activeElement !== current.trigger
    )
      this.inspection.set(null);
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
}
