import { NgTemplateOutlet } from '@angular/common';
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
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { EvidenceBadgeComponent } from '../../../shared/ui/verdict/evidence-badge.component';
import { escapeFormatChars, escapeFormatLabel } from './ref-name-display';

@Component({
  selector: 'app-run-list-table',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    RouterLink,
    CdkVirtualScrollViewport,
    CdkFixedSizeVirtualScroll,
    CdkVirtualForOf,
    RunStatePillComponent,
    StatusPillComponent,
    DataTableValueComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-list-table.component.html',
  styleUrl: './run-list-table.component.css',
})
export class RunListTableComponent {
  readonly rows = input.required<readonly RunSummary[]>();
  protected readonly virtual = computed(() => this.rows().length > 200);
  protected readonly itemSize = signal(132);
  private readonly probe = viewChild.required<ElementRef<HTMLElement>>('rowProbe');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  protected readonly displayRef = escapeFormatChars;
  /**
   * The same escaping for `rerun_label` / `reprocess_label`. Both are caller-written strings shown
   * in the row, so neither may reach the cell as a raw value (a U+202E label would reorder the rest
   * of the row). The template wraps each in `<bdi>` as well, exactly like the ref name.
   */
  protected readonly displayLabel = escapeFormatLabel;
  protected readonly trackRow = (_index: number, row: RunSummary) => row.run_id;
  protected readonly copy = {
    caption: $localize`:@@runs.list.table-caption:Project runs`,
    id: $localize`:@@runs.list.column-id:Run id`,
    state: $localize`:@@runs.list.column-state:State`,
    evidence: $localize`:@@runs.list.column-evidence:Evidence`,
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
    afterRenderEffect(() => {
      const height = Number.parseFloat(getComputedStyle(this.probe().nativeElement).height);
      // jsdom does not resolve CSS tokens; 132 mirrors three 44px touch rows in tokens.css.
      this.itemSize.set(height > 0 ? height : 132);
      this.viewport()?.checkViewportSize();
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
