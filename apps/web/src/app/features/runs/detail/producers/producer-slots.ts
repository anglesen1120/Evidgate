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

/** Declaration order is shard order; absent facts must not become zero or a result. */
export function producerSlotsFromRun(
  run: Pick<RunStateDto, 'declared_producers'>,
  facts?: Readonly<Record<string, Partial<ProducerSlotFacts> | undefined>>,
): readonly ProducerSlot[] {
  return run.declared_producers.map((producerId) => {
    const slot = facts?.[producerId];
    return {
      producerId,
      identityState: slot?.identityState ?? null,
      finalizeStatus: slot?.finalizeStatus ?? null,
      lastSequence: slot?.lastSequence ?? null,
      count: slot?.count ?? null,
      expected: slot?.expected ?? null,
      staleEpochEvents: slot?.staleEpochEvents ?? null,
      afterCancelEvents: slot?.afterCancelEvents ?? null,
    };
  });
}
