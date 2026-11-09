import type { RunStateDto } from '@evidgate/sdk';

export interface ProducerSlotFacts {
  readonly identityState: string | null;
  readonly finalizeStatus: string | null;
  readonly lastSequence: number | null;
  readonly count: number | null;
  readonly expected: number | null;
  readonly staleEpochEvents: number | null;
  readonly afterCancelEvents: number | null;
}

export interface ProducerSlot extends ProducerSlotFacts {
  readonly producerId: string;
}

export type ProducerStatus = 'loading' | 'ready' | 'error';

/**
 * Declaration order is shard order: one row per declared slot, with the served facts joined by
 * producer id. A slot the read serves no facts for keeps every fact null; null is never a zero.
 */
export function producerSlotsFromRun(
  run: Pick<RunStateDto, 'declared_producers' | 'producers'>,
): readonly ProducerSlot[] {
  const served = new Map(run.producers.map((slot) => [slot.producer_id, slot]));
  return run.declared_producers.map((producerId) => {
    const slot = served.get(producerId);
    return {
      producerId,
      identityState: slot?.identity_state ?? null,
      finalizeStatus: slot?.finalize_status ?? null,
      lastSequence: slot?.last_sequence ?? null,
      count: slot?.count ?? null,
      expected: slot?.expected ?? null,
      staleEpochEvents: slot?.stale_epoch_events ?? null,
      afterCancelEvents: slot?.after_cancel_events ?? null,
    };
  });
}
