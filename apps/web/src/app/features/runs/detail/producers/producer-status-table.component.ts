import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';

import { StatusPillComponent } from '../../../../shared/ui/chips/status-pill.component';
import { EmptyPatternComponent } from '../../../../shared/ui/states/empty-pattern.component';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { escapeFormatChars } from '../../list/ref-name-display';
import { producerAnchorId } from '../evidence/run-evidence-anchors';
import type { ProducerSlot, ProducerStatus } from './producer-slots';

@Component({
  selector: 'app-producer-status-table',
  imports: [
    HlmCard,
    HlmCardTitle,
    HlmTableImports,
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

  protected readonly producerAnchorId = producerAnchorId;
  protected readonly display = escapeFormatChars;
  protected readonly copy = {
    title: $localize`:@@runs.detail.producers.title:Producers`,
    loading: $localize`:@@runs.detail.producers.loading:Loading producers`,
    empty: $localize`:@@runs.detail.producers.empty:No declared producers`,
    emptyDescription: $localize`:@@runs.detail.producers.empty-description:This run declares no producer slots. This is not a collection result.`,
    producer: $localize`:@@runs.detail.producers.producer:Producer slot`,
    identity: $localize`:@@runs.detail.producers.identity:Bound identity state`,
    finalize: $localize`:@@runs.detail.producers.finalize:Finalize status`,
    lastSequence: $localize`:@@runs.detail.producers.last-sequence:Last sequence`,
    count: $localize`:@@runs.detail.producers.count:Count / expected`,
    mismatch: $localize`:@@runs.detail.producers.mismatch:Count does not match expected`,
    events: $localize`:@@runs.detail.producers.events:Informational events`,
    noEvents: $localize`:@@runs.detail.producers.no-events:No informational events`,
    eventNote: $localize`:@@runs.detail.producers.event-note:Stale-epoch and after-cancel event counts are informational and do not affect completeness.`,
  };

  protected staleEpochLabel(count: number): string {
    return $localize`:@@runs.detail.producers.stale-epoch:${count}:count: stale-epoch events`;
  }

  protected afterCancelLabel(count: number): string {
    return $localize`:@@runs.detail.producers.after-cancel:${count}:count: after-cancel events`;
  }
}
