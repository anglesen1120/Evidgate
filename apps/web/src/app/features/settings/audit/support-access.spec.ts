// 09.7.1-T6.1 (plan 09.7.1 §2 AC8, §9 "Support-access row flagged"; COORDINATOR-DECISION S76):
// the support-access marker of an audit row, read from the row alone.
//
// The wire has no `support_grant_id` column, so the decision is exactly two rules and this suite
// pins both directions: the metadata marker (whoever the actor is) and the platform-operator actor
// type (even with no marker). The edge cases that matter are the ones a UI would get wrong — an
// empty-string marker rendering a blank chip, and an operator row with no grant being treated as
// an ordinary row and losing AC8's flag.

import type { AuditEntryDto } from '@evidgate/sdk';

import { supportAccessOf } from './support-access';

/** One served audit row, with the fields each case overrides. */
function entry(overrides: Partial<AuditEntryDto> = {}): AuditEntryDto {
  return {
    id: 'ae-1',
    seq: 1,
    actor_type: 'user',
    actor_id: 'u-1',
    action: 'membership.invite',
    object_type: 'membership',
    object_id: 'm-1',
    before_hash: null,
    after_hash: null,
    request_id: 'req-1',
    trace_id: 'trace-1',
    metadata: null,
    prev_hash: 'h-0',
    entry_hash: 'h-1',
    created_at: '2026-10-01T09:00:00Z',
    ...overrides,
  };
}

describe('supportAccessOf (09.7.1-T6.1, AC8)', () => {
  it('is null for an ordinary member row', () => {
    expect(supportAccessOf(entry())).toBeNull();
  });

  it('is null for a member row whose metadata carries no grant marker', () => {
    expect(supportAccessOf(entry({ metadata: { reason: 'ticket-4417' } }))).toBeNull();
  });

  it('flags a row carrying the metadata marker, whoever the actor is', () => {
    const row = entry({ metadata: { support_grant_id: 'sg-7' } });
    expect(supportAccessOf(row)).toEqual({ grantId: 'sg-7' });
  });

  it('flags a non-platform actor that carries the marker', () => {
    const row = entry({ actor_type: 'agent', metadata: { support_grant_id: 'sg-9' } });
    expect(supportAccessOf(row)).toEqual({ grantId: 'sg-9' });
  });

  it('flags a platform-operator row with no marker, naming no grant', () => {
    expect(supportAccessOf(entry({ actor_type: 'platform_operator' }))).toEqual({
      grantId: null,
    });
    expect(
      supportAccessOf(entry({ actor_type: 'platform_operator', metadata: { scope: 'read' } })),
    ).toEqual({ grantId: null });
  });

  it('names the grant of a platform-operator row that carries the marker', () => {
    const row = entry({
      actor_type: 'platform_operator',
      metadata: { support_grant_id: 'sg-11', scope: 'read' },
    });
    expect(supportAccessOf(row)).toEqual({ grantId: 'sg-11' });
  });

  it('treats an empty-string marker as absent', () => {
    // An empty string is what a serializer writes for "no value"; it must not become a blank chip.
    expect(supportAccessOf(entry({ metadata: { support_grant_id: '' } }))).toBeNull();
    const operator = entry({
      actor_type: 'platform_operator',
      metadata: { support_grant_id: '' },
    });
    expect(supportAccessOf(operator)).toEqual({ grantId: null });
  });

  it('reads the marker from metadata only, never from an object id', () => {
    const row = entry({ object_type: 'support_grant', object_id: 'sg-3' });
    expect(supportAccessOf(row)).toBeNull();
  });
});
