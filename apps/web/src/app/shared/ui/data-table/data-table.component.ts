import { NgTemplateOutlet } from '@angular/common';
import { ArrayDataSource } from '@angular/cdk/collections';
import {
  CdkFixedSizeVirtualScroll,
  type CdkVirtualScrollRepeater,
  CdkVirtualScrollViewport,
} from '@angular/cdk/scrolling';
import {
  CdkCell,
  CdkCellDef,
  CdkColumnDef,
  CdkHeaderCell,
  CdkHeaderCellDef,
  CdkHeaderRow,
  CdkHeaderRowDef,
  CdkRow,
  CdkRowDef,
  CdkTable,
} from '@angular/cdk/table';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import type { ListRange } from '@angular/cdk/collections';

import { DataTableValueComponent } from './data-table-value.component';

export interface DataTableColumn<T> {
  readonly id: string;
  /** Already localized by the caller. */
  readonly header: string;
  readonly cell: (row: T) => string | number;
  readonly align?: 'start' | 'end';
  readonly truncate?: boolean;
  readonly mutates?: boolean;
}

export type DataTableDensity = 'default' | 'compact';
export type DataTableRowId<T> = (row: T) => string | number;

let nextTableId = 0;

/**
 * Render-only, signal-fed CDK table; never fetches, sorts, selects or mutates rows.
 * Caption, column labels and row values are already localized by the caller.
 *
 * Above virtualThreshold, a CDK fixed-size viewport owns windowing. Wrapped cells
 * are clipped to the fixed row height by design: variable-height rows require
 * cursor pagination of <=200 rows instead (ADR-0008 rule 8).
 *
 * A header-only CDK table remains outside the translated virtual content wrapper,
 * but inside the local horizontal scroll container. That named scroll region wraps
 * a semantic table; presentation tables combine their rows under that table role.
 * No grid keyboard interaction is implied. Coarse pointers use default density.
 */
@Component({
  selector: 'app-data-table',
  imports: [
    NgTemplateOutlet,
    CdkTable,
    CdkColumnDef,
    CdkHeaderCellDef,
    CdkHeaderCell,
    CdkCellDef,
    CdkCell,
    CdkHeaderRowDef,
    CdkHeaderRow,
    CdkRowDef,
    CdkRow,
    CdkVirtualScrollViewport,
    CdkFixedSizeVirtualScroll,
    DataTableValueComponent,
  ],
  templateUrl: './data-table.component.html',
  styleUrl: './data-table.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.compact]': 'rowHeight() === "compact"',
    '[class.virtual]': 'virtual()',
  },
})
export class DataTableComponent<T> {
  readonly columns = input.required<readonly DataTableColumn<T>[]>();
  readonly rows = input.required<readonly T[]>();
  readonly rowId = input.required<DataTableRowId<T>>();
  readonly caption = input.required<string>();
  readonly rowHeight = input<DataTableDensity>('default');
  readonly virtualThreshold = input(200);

  readonly captionId = `eg-data-table-${nextTableId++}-caption`;
  readonly columnIds = computed(() => this.columns().map((column) => column.id));
  readonly virtual = computed(() => this.rows().length > this.virtualThreshold());
  readonly emptyData: readonly T[] = [];
  readonly viewport = viewChild(CdkVirtualScrollViewport);
  private readonly scrollElement = viewChild.required<ElementRef<HTMLElement>>('scrollElement');
  private readonly tokenProbe = viewChild.required<ElementRef<HTMLElement>>('tokenProbe');
  private readonly range = signal<ListRange>({ start: 0, end: 0 });
  readonly itemSize = signal(44);
  readonly horizontalScrolls = signal(false);
  readonly viewportScrolls = signal(false);
  readonly windowStart = computed(() => (this.virtual() ? this.range().start : 0));
  private readonly visibleRows = computed(() =>
    this.virtual() ? this.rows().slice(this.range().start, this.range().end) : this.rows(),
  );
  private readonly rowsObservable = toObservable(this.rows);
  readonly dataSource = new ArrayDataSource(toObservable(this.visibleRows));
  readonly trackRow = (_index: number, row: T) => this.rowId()(row);

  constructor() {
    effect((onCleanup) => {
      const viewport = this.viewport();
      if (!viewport) return;
      const repeater: CdkVirtualScrollRepeater<T> = {
        dataStream: this.rowsObservable,
        measureRangeSize: (range) => (range.end - range.start) * this.itemSize(),
      };
      // The public CDK repeater contract lets the table render the bounded window
      // itself, without a second cdkVirtualFor renderer or private CDK APIs.
      const subscription = viewport.renderedRangeStream.subscribe((range) => {
        const active = document.activeElement;
        if (active instanceof HTMLElement && viewport.elementRef.nativeElement.contains(active)) {
          const row = active.closest('[data-row-id]');
          const index = Number(row?.getAttribute('aria-rowindex')) - 2;
          if (row && (index < range.start || index >= range.end)) {
            // Only restore focus when that row leaves the window; overlapping
            // windows must not steal focus from a still-visible clipped value.
            viewport.elementRef.nativeElement.focus({ preventScroll: true });
          }
        }
        this.range.set(range);
      });
      viewport.attach(repeater);
      onCleanup(() => {
        subscription.unsubscribe();
        viewport.detach();
      });
    });

    afterRenderEffect((onCleanup) => {
      this.rowHeight();
      this.rows();
      this.columns();
      const scroll = this.scrollElement().nativeElement;
      const probe = this.tokenProbe().nativeElement;
      const viewport = this.viewport();
      const measure = () => {
        // CSS resolves --eg-row-* (including coarse-pointer density) into pixels.
        // jsdom has no token/layout resolution: these fallbacks mirror tokens.css.
        const pixels = Number.parseFloat(getComputedStyle(probe).height);
        this.itemSize.set(pixels > 0 ? pixels : this.rowHeight() === 'compact' ? 40 : 44);
        this.horizontalScrolls.set(
          scroll.clientWidth > 0 &&
            (scroll.scrollWidth > scroll.clientWidth || scroll.scrollHeight > scroll.clientHeight),
        );
        if (viewport) {
          const element = viewport.elementRef.nativeElement;
          viewport.checkViewportSize();
          this.viewportScrolls.set(
            element.clientHeight > 0 && element.scrollHeight > element.clientHeight,
          );
        }
      };
      measure();
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(measure);
      observer.observe(scroll);
      observer.observe(probe);
      if (viewport) observer.observe(viewport.elementRef.nativeElement);
      onCleanup(() => observer.disconnect());
    });
  }
}
