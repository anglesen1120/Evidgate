import { producerSlotsFromRun } from './producer-slots';

describe('producerSlotsFromRun', () => {
  it('keeps declared shard order and makes every unserved fact unavailable', () => {
    const rows = producerSlotsFromRun({ declared_producers: ['shard-9', 'shard-2'] });
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

  it('preserves supplied zero values and does not introduce undeclared slots', () => {
    const facts = {
      'shard-2': { count: 0, expected: 0, lastSequence: 0, afterCancelEvents: 0 },
      undeclared: { count: 99 },
    };
    const rows = producerSlotsFromRun({ declared_producers: ['shard-2'] }, facts);
    expect(rows).toEqual([
      {
        producerId: 'shard-2',
        identityState: null,
        finalizeStatus: null,
        lastSequence: 0,
        count: 0,
        expected: 0,
        staleEpochEvents: null,
        afterCancelEvents: 0,
      },
    ]);
  });

  it('returns empty only for an empty declaration', () => {
    expect(producerSlotsFromRun({ declared_producers: [] })).toEqual([]);
  });
});
