import { DOCUMENT, formatNumber, NgTemplateOutlet } from '@angular/common';
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
  afterNextRender,
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  LOCALE_ID,
  signal,
  viewChild,
} from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import type { ListRange } from '@angular/cdk/collections';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmTable,
  HlmTableColumn,
  HlmTableContainer,
  HlmTd,
  HlmTh,
  HlmTr,
} from '@evidgate/ui/table';
import { hlm } from '@evidgate/ui/utils';

import { DataTableCellComponent } from './data-table-cell.component';
import { resolveTableColumns } from './data-table-column';
import type { DataTableColumn } from './data-table-column';
import {
  captureTableLayoutFocus,
  type DataTableLayoutFocus,
  restoreTableLayoutFocus,
} from './data-table-layout-focus';
import { DataTableRecordsComponent } from './data-table-records.component';

export type { DataTableColumn } from './data-table-column';

export type DataTableDensity = 'default' | 'compact';
export type DataTableFlow = 'bounded' | 'page';
export type DataTableRowId<T> = (row: T) => string | number;

let nextTableId = 0;

/**
 * Render-only, signal-fed CDK table; never fetches, sorts, selects or mutates rows.
 * Caption, column labels and row values are already localized by the caller.
 *
 * Fixed-size rows always use recoverable one-line previews. Custom cells opt in
 * only when their caller guarantees that stride; variable-height cells and
 * narrow records retain bounded pages of already loaded rows.
 *
 * A header-only table sits outside the translated virtual window. Ordinary mode
 * uses one native table so utility columns size intrinsically. Both keep the
 * header inside the named local scroller, without implying grid interaction.
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
    DataTableCellComponent,
    DataTableRecordsComponent,
    HlmButton,
    HlmTableColumn,
    HlmTable,
    HlmTableContainer,
    HlmTd,
    HlmTh,
    HlmTr,
  ],
  templateUrl: './data-table.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'isolate block min-w-0 bg-card font-sans text-sm text-foreground [--eg-table-row:var(--eg-row-default)] [--eg-table-inset:var(--eg-space-3)] [&.compact]:[--eg-table-row:var(--eg-row-compact)] [&.compact]:[--eg-table-inset:var(--eg-space-2)] pointer-coarse:[&.compact]:[--eg-table-row:var(--eg-row-default)] pointer-coarse:[&.compact]:[--eg-table-inset:var(--eg-space-3)]',
    '[class.compact]': 'rowHeight() === "compact"',
    '[class.virtual]': 'virtual()',
    '[style.--eg-table-header-height.px]': 'flow() === "page" && !virtual() ? 0 : headerHeight()',
  },
})
export class DataTableComponent<T> {
  private readonly locale = inject(LOCALE_ID);

  private readonly document = inject(DOCUMENT);

  private readonly injector = inject(Injector);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly columns = input.required<readonly DataTableColumn<T>[]>();
  readonly rows = input.required<readonly T[]>();
  readonly rowId = input.required<DataTableRowId<T>>();
  readonly caption = input.required<string>();
  readonly rowHeight = input<DataTableDensity>('default');
  readonly virtualThreshold = input(200);
  /** Page flow has no inner row cap or sticky header; virtual windows keep their contract. */
  readonly flow = input<DataTableFlow>('bounded');
  /** Opt ordinary tables into record reflow when readable columns cannot fit their container. */
  readonly fitToContainer = input(false);
  /** Opt in only when every custom cell fits the fixed row stride. */
  readonly fixedHeightCells = input(false);

  readonly captionId = `eg-data-table-${nextTableId++}-caption`;
  readonly narrow = signal(false);
  private readonly customCells = computed(() =>
    this.columns().some((column) => column.cellTemplate),
  );
  readonly virtual = computed(
    () =>
      !this.narrow() &&
      (!this.customCells() || this.fixedHeightCells()) &&
      this.rows().length > this.virtualThreshold(),
  );
  readonly resolvedColumns = computed(() =>
    resolveTableColumns(this.columns(), this.virtual() ? this.rows() : []),
  );
  readonly columnIds = computed(() => this.columns().map((column) => column.id));
  readonly emptyData: readonly T[] = [];
  readonly viewport = viewChild(CdkVirtualScrollViewport);
  readonly itemSize = signal(44);
  protected readonly headerHeight = signal(44);
  readonly horizontalScrolls = signal(false);
  readonly viewportScrolls = signal(false);
  protected readonly pageSize = 200;
  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.rows().length / this.pageSize)),
  );
  protected readonly pageIndex = linkedSignal<number, number>({
    source: this.pageCount,
    computation: (pages, previous) => Math.min(previous?.value ?? 0, pages - 1),
  });
  protected readonly paged = computed(() => !this.virtual() && this.pageCount() > 1);
  protected readonly pageRows = computed(() => {
    const rows = this.rows();
    if (!this.paged()) return rows;
    const start = this.pageIndex() * this.pageSize;
    return rows.slice(start, start + this.pageSize);
  });
  protected readonly cellClasses =
    'box-border min-w-0 h-[var(--eg-table-row)] px-[var(--eg-space-4)] py-[var(--eg-table-inset)] align-top whitespace-normal [overflow-wrap:anywhere] border-b border-border [&.numeric]:text-right [&.numeric]:tabular-nums';
  protected readonly bodyCellClasses = computed(() =>
    hlm(this.cellClasses, this.virtual() ? 'py-0 border-b-0 shadow-sm' : ''),
  );
  protected readonly virtualMinWidth = computed(() => {
    const widths = this.resolvedColumns().flatMap((column) =>
      column.virtualWidth === null ? ['9.5rem'] : [column.virtualWidth],
    );
    return this.virtual() ? `max(40rem, calc(${widths.join(' + ') || '0px'}))` : null;
  });
  protected readonly probeText = 'Ág';
  protected readonly previousLabel = $localize`:@@shared.data-table.previous:Previous records`;
  protected readonly nextLabel = $localize`:@@shared.data-table.next:Next records`;
  protected readonly pagesLabel = computed(() => {
    const caption = this.caption();
    return $localize`:@@shared.data-table.pages:${caption}:caption: — loaded record pages`;
  });
  protected readonly pageLabel = computed(() => {
    const total = this.rows().length;
    const start = formatNumber(this.pageIndex() * this.pageSize + 1, this.locale);
    const end = formatNumber(Math.min((this.pageIndex() + 1) * this.pageSize, total), this.locale);
    const loaded = formatNumber(total, this.locale);
    return $localize`:@@shared.data-table.loaded-range:Loaded records ${start}:start:–${end}:end: of ${loaded}:loaded:`;
  });
  private readonly previousButton = viewChild<ElementRef<HTMLButtonElement>>('previousPage');
  private readonly nextButton = viewChild<ElementRef<HTMLButtonElement>>('nextPage');
  private readonly scrollElement = viewChild.required<ElementRef<HTMLElement>>('scrollElement');
  private readonly tokenProbe = viewChild.required<ElementRef<HTMLElement>>('tokenProbe');
  private readonly range = signal<ListRange>({ start: 0, end: 0 });
  private layoutFocus: DataTableLayoutFocus | undefined;
  private fitColumns: readonly DataTableColumn<T>[] | undefined;
  private fitFont = '';
  private fitProbeWidth = 0;
  private minimumFitWidth = 0;
  readonly windowStart = computed(() =>
    this.virtual() ? this.range().start : this.pageIndex() * this.pageSize,
  );
  private readonly visibleRows = computed(() =>
    this.virtual() ? this.rows().slice(this.range().start, this.range().end) : this.pageRows(),
  );
  private readonly rowsObservable = toObservable(this.rows);
  readonly dataSource = new ArrayDataSource(toObservable(this.visibleRows));
  readonly trackRow = (_index: number, row: T) => this.rowId()(row);

  constructor() {
    effect((onCleanup) => {
      const viewport = this.viewport();
      if (viewport) onCleanup(this.attachViewport(viewport));
    });
    afterRenderEffect((onCleanup) => {
      const scroll = this.scrollElement().nativeElement;
      const probe = this.tokenProbe().nativeElement;
      const viewport = this.viewport();
      this.narrow();
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(() => this.measure());
      observer.observe(this.host.nativeElement);
      observer.observe(scroll);
      observer.observe(probe);
      const header = this.host.nativeElement.querySelector('.eg-table-sticky');
      if (header) observer.observe(header);
      if (viewport) observer.observe(viewport.elementRef.nativeElement);
      onCleanup(() => observer.disconnect());
    });
    afterRenderEffect(() => {
      this.rowHeight();
      this.rows();
      this.columns();
      this.measure();
    });
    afterRenderEffect(() => this.demoteVirtualSections());
  }

  revealRow(index: number): void {
    if (index < 0 || index >= this.rows().length) return;
    this.pageIndex.set(Math.floor(index / this.pageSize));
    this.viewport()?.scrollToIndex(index);
  }

  protected movePage(delta: number): void {
    const source = (delta < 0 ? this.previousButton() : this.nextButton())?.nativeElement;
    const owned = source !== undefined && this.document.activeElement === source;
    this.pageIndex.update((page) => Math.max(0, Math.min(page + delta, this.pageCount() - 1)));
    this.scrollElement().nativeElement.scrollTop = 0;
    if (!owned) return;
    // A boundary disables the activated button; restore its place only if the
    // visitor has not deliberately moved focus elsewhere before the render.
    afterNextRender(
      () => {
        if (!source?.disabled) return;
        const active: unknown = this.document.activeElement;
        const lost =
          !(active instanceof HTMLElement) ||
          active === this.document.body ||
          !active.isConnected ||
          active === source;
        if (!lost) return;
        const target = (delta < 0 ? this.nextButton() : this.previousButton())?.nativeElement;
        if (target?.isConnected && !target.disabled) target.focus();
      },
      { injector: this.injector },
    );
  }

  private attachViewport(viewport: CdkVirtualScrollViewport): () => void {
    const repeater: CdkVirtualScrollRepeater<T> = {
      dataStream: this.rowsObservable,
      measureRangeSize: (range) => (range.end - range.start) * this.itemSize(),
    };
    const subscription = viewport.renderedRangeStream.subscribe((range) => {
      const active = document.activeElement;
      if (active instanceof HTMLElement && viewport.elementRef.nativeElement.contains(active)) {
        const row = active.closest('[data-row-id]');
        const index = Number(row?.getAttribute('aria-rowindex')) - 2;
        if (row && (index < range.start || index >= range.end)) {
          viewport.elementRef.nativeElement.focus({ preventScroll: true });
        }
      }
      this.range.set(range);
      if (this.layoutFocus) {
        afterNextRender(() => this.restoreLayoutFocus(), { injector: this.injector });
      }
    });
    viewport.attach(repeater);
    return () => {
      subscription.unsubscribe();
      viewport.detach();
    };
  }

  private updateLayout(narrow: boolean): void {
    if (narrow === this.narrow()) return;
    const anchor = this.captureLayoutFocus() ?? this.layoutFocus;
    this.layoutFocus = anchor;
    if (anchor) this.pageIndex.set(Math.floor(anchor.rowIndex / this.pageSize));
    this.narrow.set(narrow);
    if (!anchor) return;
    afterNextRender(
      () => {
        if (this.layoutFocus !== anchor) return;
        this.restoreLayoutFocus();
        // A paged record can return to a virtual window far beyond its first rows.
        const viewport = this.viewport();
        if (this.layoutFocus === anchor && viewport) {
          const index = this.rows().findIndex((row) => String(this.rowId()(row)) === anchor.rowId);
          if (index >= 0) viewport.scrollToIndex(index);
        }
      },
      { injector: this.injector },
    );
  }

  private captureLayoutFocus(): DataTableLayoutFocus | undefined {
    return captureTableLayoutFocus(
      this.document,
      this.scrollElement().nativeElement,
      this.rows(),
      this.rowId(),
    );
  }

  private restoreLayoutFocus(): void {
    const anchor = this.layoutFocus;
    if (!anchor) return;
    restoreTableLayoutFocus(this.document, this.scrollElement().nativeElement, anchor, {
      virtual: this.virtual(),
      rowExists: (rowId) => this.rows().some((row) => String(this.rowId()(row)) === rowId),
      clear: () => {
        this.layoutFocus = undefined;
      },
    });
  }

  private measure(): void {
    const scroll = this.scrollElement().nativeElement;
    const probe = this.tokenProbe().nativeElement;
    const viewport = this.viewport();
    const width = this.host.nativeElement.clientWidth;
    const probeStyle = getComputedStyle(probe);
    const fittedRecords = this.needsFittedRecords(scroll, width, probeStyle);
    if (width > 0) this.updateLayout(width < 768 || fittedRecords);
    const header = this.host.nativeElement.querySelector('.eg-table-sticky');
    const height = header?.getBoundingClientRect().height ?? 0;
    if (height > 0) this.headerHeight.set(height);
    // Text resizing grows the virtual stride rather than clipping a 200% line.
    const pixels = Number.parseFloat(probeStyle.height);
    const minimum = this.rowHeight() === 'compact' ? 40 : 44;
    this.itemSize.set(pixels > 0 ? Math.max(minimum, pixels) : minimum);
    this.horizontalScrolls.set(
      !this.narrow() &&
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
  }

  private needsFittedRecords(
    scroll: HTMLElement,
    width: number,
    probeStyle: CSSStyleDeclaration,
  ): boolean {
    const columns = this.columns();
    const rows = this.rows();
    // A hidden host or zero-sized probe is not a new usable font measurement.
    const visible = width > 0 && scroll.clientWidth > 0;
    const probeWidth =
      visible && this.fitToContainer() ? Number.parseFloat(probeStyle.width) || 0 : 0;
    const font = visible && Number.parseFloat(probeStyle.fontSize) > 0 ? probeStyle.font : '';
    if (
      columns !== this.fitColumns ||
      (font !== '' && font !== this.fitFont) ||
      (probeWidth > 0 && probeWidth !== this.fitProbeWidth)
    ) {
      this.minimumFitWidth = 0;
    }
    this.fitColumns = columns;
    if (font !== '') this.fitFont = font;
    if (probeWidth > 0) this.fitProbeWidth = probeWidth;
    // A row-only change keeps records mounted: do not rebuild up to 200 rich rows
    // just to rediscover the same minimum. Column or font changes may re-probe.
    const fit =
      this.fitToContainer() &&
      ((!this.fixedHeightCells() && this.customCells()) || rows.length <= this.virtualThreshold());
    if (
      fit &&
      !this.narrow() &&
      scroll.clientWidth > 0 &&
      scroll.scrollWidth > scroll.clientWidth + 1
    ) {
      this.minimumFitWidth = Math.max(
        this.minimumFitWidth,
        Math.ceil(scroll.scrollWidth + width - scroll.clientWidth),
      );
    }
    return fit && width < this.minimumFitWidth;
  }

  private demoteVirtualSections(): void {
    const viewport = this.viewport();
    if (!viewport) return;
    // The viewport owns the virtual rowgroup; CDK's native sections must not nest
    // another rowgroup inside it. Ordinary tables retain CDK's native semantics.
    for (const section of viewport.elementRef.nativeElement.querySelectorAll(
      'thead[role="rowgroup"], tbody[role="rowgroup"], tfoot[role="rowgroup"]',
    )) {
      section.setAttribute('role', 'presentation');
    }
  }
}
