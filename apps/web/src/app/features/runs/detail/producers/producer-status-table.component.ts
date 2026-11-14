import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';

import { StatusPillComponent } from '../../../../shared/ui/chips/status-pill.component';
import { EmptyPatternComponent } from '../../../../shared/ui/states/empty-pattern.component';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { escapeFormatChars } from '../../list/ref-name-display';
import { producerAnchorId } from '../evidence/run-evidence-anchors';
import { RUN_DETAIL_NOT_PROVIDED, runValueLabel } from '../run-value-label';
import type { ProducerSlot, ProducerStatus } from './producer-slots';

@Component({
  selector: 'app-producer-status-table',
  imports: [
    HlmCard,
    HlmCardTitle,
    HlmTableImports,
    HlmButton,
    StatusPillComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    LoadingPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './producer-status-table.component.html',
  host: { class: 'block min-w-0 max-w-full text-foreground wrap-anywhere' },
})
export class ProducerStatusTableComponent {
  readonly slots = input<readonly ProducerSlot[] | null>(null);
  readonly status = input<ProducerStatus>('ready');
  readonly runAfterCancelEvents = input<number | null>(null);
  readonly targetAnchor = input<string | null>(null);

  // Variable-height badges need bounded pages rather than clipped virtual rows.
  // A fragment selects its page once; ordinary polling preserves the reader's page.
  protected readonly pageSize = 200;
  protected readonly pageCount = computed(() => {
    return Math.max(1, Math.ceil((this.slots()?.length ?? 0) / this.pageSize));
  });
  protected readonly pageIndex = linkedSignal<
    { anchor: string | null; slots: readonly ProducerSlot[] | null },
    number
  >({
    source: () => ({ anchor: this.targetAnchor(), slots: this.slots() }),
    computation: ({ anchor, slots }, previous) => {
      const lastPage = Math.max(0, Math.ceil((slots?.length ?? 0) / this.pageSize) - 1);
      if (
        anchor !== null &&
        (previous === undefined ||
          previous.source.anchor !== anchor ||
          previous.source.slots === null)
      ) {
        const index =
          slots?.findIndex((slot) => producerAnchorId(slot.producerId) === anchor) ?? -1;
        if (index >= 0) return Math.floor(index / this.pageSize);
      }
      return Math.min(previous?.value ?? 0, lastPage);
    },
  });
  protected readonly pageSlots = computed(() => {
    const slots = this.slots() ?? [];
    const start = this.pageIndex() * this.pageSize;
    return slots.length <= this.pageSize ? slots : slots.slice(start, start + this.pageSize);
  });
  protected readonly pageLabel = computed(() => {
    const page = this.pageIndex() + 1;
    const pages = this.pageCount();
    return $localize`:@@runs.detail.producers.page:Page ${page}:page: of ${pages}:pages:`;
  });

  protected readonly producerAnchorId = producerAnchorId;
  protected readonly display = escapeFormatChars;
  protected readonly valueLabel = runValueLabel;
  protected readonly copy = {
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    title: $localize`:@@runs.detail.producers.title:Producers`,
    loading: $localize`:@@runs.detail.producers.loading:Loading producers`,
    empty: $localize`:@@runs.detail.producers.empty:No declared producers`,
    emptyDescription: $localize`:@@runs.detail.producers.empty-description:This run declares no producer slots. This is not a collection result.`,
    producer: $localize`:@@runs.detail.producers.producer:Producer slot`,
    identity: $localize`:@@runs.detail.producers.identity:Bound identity state`,
    finalize: $localize`:@@runs.detail.producers.finalize:Finalize status`,
    lastSequence: $localize`:@@runs.detail.producers.last-sequence:Last sequence`,
    count: $localize`:@@runs.detail.producers.count:Count / expected`,
    openSlot: $localize`:@@runs.detail.producers.open-slot:(expected not set)`,
    mismatch: $localize`:@@runs.detail.producers.mismatch:Count does not match expected`,
    events: $localize`:@@runs.detail.producers.events:Informational events`,
    noEvents: $localize`:@@runs.detail.producers.no-events:No informational events`,
    eventNote: $localize`:@@runs.detail.producers.event-note:After-cancel event counts are informational only; a stale-epoch event on a closed shard makes the evidence incomplete.`,
    previous: $localize`:@@runs.detail.producers.previous:Previous producers`,
    next: $localize`:@@runs.detail.producers.next:Next producers`,
    pagination: $localize`:@@runs.detail.producers.pagination:Producer pages`,
  };

  protected movePage(delta: number): void {
    this.pageIndex.update((page) => Math.max(0, Math.min(page + delta, this.pageCount() - 1)));
  }

  protected openCountLabel(count: number): string {
    return $localize`:@@runs.detail.producers.open-count:${count}:count: served, expected count not set yet`;
  }

  protected staleEpochLabel(count: number): string {
    return count === 1
      ? $localize`:@@runs.detail.producers.stale-epoch-one:1 stale-epoch event`
      : $localize`:@@runs.detail.producers.stale-epoch:${count}:count: stale-epoch events`;
  }

  protected afterCancelLabel(count: number): string {
    return count === 1
      ? $localize`:@@runs.detail.producers.after-cancel-one:1 after-cancel event`
      : $localize`:@@runs.detail.producers.after-cancel:${count}:count: after-cancel events`;
  }

  protected runAfterCancelLabel(count: number): string {
    return count === 1
      ? $localize`:@@runs.detail.producers.run-after-cancel-one:1 after-cancel event for this run`
      : $localize`:@@runs.detail.producers.run-after-cancel:${count}:count: after-cancel events for this run`;
  }
}
