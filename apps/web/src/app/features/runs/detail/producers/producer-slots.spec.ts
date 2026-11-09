import type { RunProducerSlot } from '@evidgate/sdk';
import { producerSlotsFromRun } from './producer-slots';

const finalized: RunProducerSlot = {
  producer_id: 'shard-2',
  identity_state: 'bound',
  finalize_status: 'finalized',
  last_sequence: 9,
  count: 9,
  expected: 9,
  stale_epoch_events: 0,
  after_cancel_events: 0,
};

describe('producerSlotsFromRun', () => {
  it('keeps declared shard order and leaves a slot with no served facts unavailable', () => {
    const rows = producerSlotsFromRun({
      declared_producers: ['shard-9', 'shard-2'],
      producers: [finalized],
    });
    expect(rows.map((row) => row.producerId)).toEqual(['shard-9', 'shard-2']);
    expect(rows[0]).toEqual({
      producerId: 'shard-9',
      identityState: null,
      finalizeStatus: null,
      lastSequence: null,
      count: null,
      expected: null,
      staleEpochEvents: null,
      afterCancelEvents: null,
    });
  });

  it('carries the served facts, preserves zeros and null expected, and adds no undeclared slot', () => {
    const open: RunProducerSlot = {
      ...finalized,
      identity_state: 'unbound',
      finalize_status: 'open',
      last_sequence: 0,
      count: 0,
      expected: null,
    };
    const rows = producerSlotsFromRun({
      declared_producers: ['shard-2'],
      producers: [open, { ...finalized, producer_id: 'undeclared', count: 99 }],
    });
    expect(rows).toEqual([
      {
        producerId: 'shard-2',
        identityState: 'unbound',
        finalizeStatus: 'open',
        lastSequence: 0,
        count: 0,
        expected: null,
        staleEpochEvents: 0,
        afterCancelEvents: 0,
      },
    ]);
  });

  it('maps a finalized slot field for field, including per-slot late counts', () => {
    const [row] = producerSlotsFromRun({
      declared_producers: ['shard-2'],
      producers: [{ ...finalized, count: 7, stale_epoch_events: 2, after_cancel_events: 1 }],
    });
    expect(row).toEqual({
      producerId: 'shard-2',
      identityState: 'bound',
      finalizeStatus: 'finalized',
      lastSequence: 9,
      count: 7,
      expected: 9,
      staleEpochEvents: 2,
      afterCancelEvents: 1,
    });
  });

  it('returns empty only for an empty declaration', () => {
    expect(producerSlotsFromRun({ declared_producers: [], producers: [] })).toEqual([]);
  });
});
