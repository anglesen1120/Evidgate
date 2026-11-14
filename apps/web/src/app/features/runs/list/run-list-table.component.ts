import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  CdkFixedSizeVirtualScroll,
  CdkVirtualForOf,
  CdkVirtualScrollViewport,
} from '@angular/cdk/scrolling';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { RunSummary } from '@evidgate/sdk';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmTableImports } from '@evidgate/ui/table';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { escapeFormatChars, escapeFormatLabel } from './ref-name-display';

@Component({
  selector: 'app-run-list-table',
  imports: [
    NgTemplateOutlet,
    DatePipe,
    RouterLink,
    CdkVirtualScrollViewport,
    CdkFixedSizeVirtualScroll,
    CdkVirtualForOf,
    HlmBadge,
    HlmTableImports,
    RunStatePillComponent,
    StatusPillComponent,
    DataTableValueComponent,
    DecisionChipComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-list-table.component.html',
  host: { class: 'block min-w-0' },
})
export class RunListTableComponent {
  readonly rows = input.required<readonly RunSummary[]>();
  protected readonly virtual = computed(() => this.rows().length > 200);
  protected readonly itemSize = signal(132);
  private readonly probe = viewChild<ElementRef<HTMLElement>>('rowProbe');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  protected readonly displayRef = escapeFormatChars;
  protected readonly cellClasses =
    'min-w-0 h-auto box-border p-[var(--eg-space-3)] whitespace-normal text-start font-(weight:--eg-weight-regular) [overflow-wrap:anywhere] [@media(max-width:767px)]:grid [@media(max-width:767px)]:gap-[var(--eg-space-2)] [@media(max-width:767px)]:p-0 before:content-[attr(data-label)] before:text-muted-foreground before:font-[family-name:var(--eg-font-body)] before:font-(weight:--eg-weight-semibold) [@media(min-width:768px)]:before:hidden';
  protected readonly columnClasses =
    'h-auto min-w-0 whitespace-normal p-[var(--eg-space-3)] text-xs font-(weight:--eg-weight-semibold) text-muted-foreground';
  // Detached header and body tables share tracks with readable floors before local scrolling.
  // Explicit media queries keep the stacked layout and hidden header aligned at 767 px.
  protected readonly rowClasses =
    'run-row grid grid-cols-[minmax(calc(19ch_+_var(--eg-space-3)*2),1.2fr)_minmax(calc(14ch_+_var(--eg-space-3)*2),.8fr)_minmax(calc(16ch_+_var(--eg-space-3)*2),1fr)_minmax(calc(19ch_+_var(--eg-space-3)*2),1.1fr)_minmax(19ch,1fr)_minmax(14ch,1fr)_minmax(calc(15ch_+_var(--eg-space-3)*2),1fr)] [@media(max-width:767px)]:grid-cols-[minmax(0,1fr)]';
  /**
   * The same escaping for `rerun_label` / `reprocess_label`. Both are caller-written strings shown
   * in the row, so neither may reach the cell as a raw value (a U+202E label would reorder the rest
   * of the row). The template wraps each in `<bdi>` as well, exactly like the ref name.
   */
  protected readonly displayLabel = escapeFormatLabel;
  protected readonly trackRow = (_index: number, row: RunSummary) => row.run_id;
  /**
   * Scrolls the table's columns from the keyboard.
   *
   * Nothing in the row cells is focusable past the first column, so the wrapper that holds the
   * horizontal overflow is the only keyboard entry to the rest of a row - and Chromium does not
   * move a focused element that is not the page's scroller on an arrow key, so the movement is
   * explicit. It acts only while the wrapper itself holds focus: a key pressed inside the virtual
   * viewport (its own End/PageDown) or on a row link bubbles through here and must keep its meaning.
   */
  protected scrollColumns(event: KeyboardEvent): void {
    const wrapper = event.currentTarget;
    if (!(wrapper instanceof HTMLElement) || event.target !== wrapper) return;
    const reach = wrapper.scrollWidth - wrapper.clientWidth;
    if (reach <= 0) return;
    const step = Math.max(80, Math.round(wrapper.clientWidth / 2));
    const next: number | undefined =
      event.key === 'ArrowRight'
        ? Math.min(wrapper.scrollLeft + step, reach)
        : event.key === 'ArrowLeft'
          ? Math.max(wrapper.scrollLeft - step, 0)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? reach
              : undefined;
    if (next === undefined || next === wrapper.scrollLeft) return;
    event.preventDefault();
    wrapper.scrollLeft = next;
  }

  protected readonly copy = {
    caption: $localize`:@@runs.list.table-caption:Project runs`,
    // The virtual viewport is a second keyboard stop inside the same table, so it needs its own
    // name: two stops announcing "Project runs" would read as one table entered twice.
    rows: $localize`:@@runs.list.table-rows:Project runs rows`,
    id: $localize`:@@runs.list.column-id:Run id`,
    state: $localize`:@@runs.list.column-state:State`,
    evidence: $localize`:@@runs.list.column-evidence:Evidence`,
    evidenceUnavailable: $localize`:@@runs.list.evidence-unavailable:Unavailable`,
    evidenceHint: $localize`:@@runs.list.evidence-hint:Open the run for evidence completeness.`,
    utc: $localize`:@@runs.list.utc:UTC`,
    decision: $localize`:@@runs.list.column-decision:Decision`,
    ref: $localize`:@@runs.list.column-ref:Ref`,
    scope: $localize`:@@runs.list.column-scope:Run scope`,
    created: $localize`:@@runs.list.column-created:Created (UTC)`,
    unverified: $localize`:@@runs.list.unverified-ref:Unverified ref`,
    unavailableRef: $localize`:@@runs.list.ref-unavailable:Ref unavailable`,
    original: $localize`:@@runs.list.original:Original run`,
    rerun: $localize`:@@runs.list.rerun:Rerun`,
    reprocess: $localize`:@@runs.list.reprocess:Reprocessed`,
    child: $localize`:@@runs.list.child:Derived run`,
  };

  constructor() {
    afterRenderEffect((onCleanup) => {
      const viewport = this.viewport();
      const probe = this.probe()?.nativeElement;
      if (!viewport || !probe) return;
      const measure = () => {
        const height = Number.parseFloat(getComputedStyle(probe).height);
        // The probe follows the responsive row size; jsdom leaves token values unresolved.
        this.itemSize.set(height > 0 ? height : 132);
        viewport.checkViewportSize();
      };
      measure();
      if (typeof ResizeObserver !== 'undefined') {
        const observer = new ResizeObserver(measure);
        observer.observe(probe);
        onCleanup(() => observer.disconnect());
      }
    });
    effect((onCleanup) => {
      const viewport = this.viewport();
      if (!viewport) return;
      const subscription = viewport.renderedRangeStream.subscribe((range) => {
        const active = document.activeElement;
        if (!(active instanceof HTMLElement) || !viewport.elementRef.nativeElement.contains(active))
          return;
        const row = active.closest('[data-run-row]');
        const index = Number(row?.getAttribute('aria-rowindex')) - 2;
        if (row && (index < range.start || index >= range.end))
          viewport.elementRef.nativeElement.focus({ preventScroll: true });
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
