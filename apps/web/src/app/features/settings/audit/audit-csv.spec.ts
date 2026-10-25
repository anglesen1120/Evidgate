// 09.7.1-T6.2 (plan 09.7.1 §2 AC7, §9 "Audit export cap"; COORDINATOR-DECISION S76): the CSV
// document the bounded export builds.
//
// Two properties are load-bearing and both are attacked here rather than assumed:
//
// 1. **No cell can become a formula.** An audit `action` or a `metadata` value is attacker-adjacent
//    (a webhook label, a case title) and a spreadsheet evaluates `=`, `+`, `-`, `@`, TAB and CR as
//    the start of one — the server export prefixes those cells with an apostrophe, and this
//    client-side export repeats the rule because it does not call the server export.
// 2. **The layout survives content.** A quote, a comma or a newline inside a value must not shift a
//    column or split a row: RFC 4180 quoting, `\r\n` line ends.
//
// The `support_grant_id` column is AC8's marker (see `support-access.spec.ts`), and `metadata` is
// the row's map as JSON, the only lossless single-cell form.

import type { AuditEntryDto } from '@evidgate/sdk';

import { toAuditCsv } from './audit-csv';

/** The header every document starts with. */
const HEADER =
  'seq,created_at,actor_type,actor_id,action,object_type,object_id,request_id,support_grant_id,metadata';

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

/** The document as lines, the trailing `\r\n` included as an empty last element. */
function lines(csv: string): string[] {
  return csv.split('\r\n');
}

describe('toAuditCsv (09.7.1-T6.2, AC7)', () => {
  it('writes the header and one line per row, in the pinned column order', () => {
    const csv = toAuditCsv([entry()]);

    expect(lines(csv)).toEqual([
      HEADER,
      '1,2026-10-01T09:00:00Z,user,u-1,membership.invite,membership,m-1,req-1,,',
      '',
    ]);
  });

  it('is a header-only document for no rows', () => {
    expect(toAuditCsv([])).toBe(`${HEADER}\r\n`);
  });

  it('prefixes a cell that a spreadsheet would evaluate as a formula', () => {
    for (const guarded of ['=1+1', '+1', '-1', '@SUM(A1)']) {
      const csv = toAuditCsv([entry({ action: guarded })]);
      expect(lines(csv)[1]).toContain(`,user,u-1,'${guarded},`);
    }
    expect(lines(toAuditCsv([entry({ action: '\tx' })]))[1]).toContain(",u-1,'\tx,");
    expect(lines(toAuditCsv([entry({ action: '\rx' })]))[1]).toContain(',u-1,"\'\rx",');
  });

  it('does not touch a value that merely contains a formula character', () => {
    expect(lines(toAuditCsv([entry({ action: 'a=b' })]))[1]).toContain(',u-1,a=b,');
  });

  it('quotes and doubles quotes when a value holds a comma, a quote or a newline', () => {
    expect(lines(toAuditCsv([entry({ actor_id: 'a,b' })]))[1]).toContain(',"a,b",');
    expect(lines(toAuditCsv([entry({ actor_id: 'a"b' })]))[1]).toContain(',"a""b",');
    expect(toAuditCsv([entry({ actor_id: 'a\nb' })])).toContain('"a\nb"');
  });

  it('keeps a formula prefix inside the quoted cell when the value also needs quoting', () => {
    // `'` first, then RFC 4180 quoting of the guarded value: "'=SUM(A1),""x""" in the cell.
    expect(toAuditCsv([entry({ action: '=SUM(A1),"x"' })])).toContain(`,"'=SUM(A1),""x""",`);
  });

  it('writes metadata as one JSON cell', () => {
    const csv = toAuditCsv([entry({ metadata: { reason: 'ticket-4417', scope: 'read' } })]);

    // The JSON string contains a comma and quotes, so the cell is quoted and its quotes doubled.
    expect(lines(csv)[1]).toContain(',"{""reason"":""ticket-4417"",""scope"":""read""}"');
  });

  it('fills the support_grant_id column from the metadata marker', () => {
    const withGrant = entry({
      actor_type: 'platform_operator',
      metadata: { support_grant_id: 'sg-7' },
    });
    const withoutGrant = entry();

    expect(lines(toAuditCsv([withGrant, withoutGrant]))[1]).toContain(
      'platform_operator,u-1,membership.invite,membership,m-1,req-1,sg-7,',
    );
    // An ordinary row leaves the column empty rather than writing "null".
    expect(lines(toAuditCsv([withoutGrant]))[1]).toContain('req-1,,');
  });

  it('leaves an absent object, request or metadata cell empty', () => {
    const csv = toAuditCsv([
      entry({
        object_type: null,
        object_id: null,
        request_id: null,
        metadata: null,
      }),
    ]);

    expect(lines(csv)[1]).toBe('1,2026-10-01T09:00:00Z,user,u-1,membership.invite,,,,,');
  });

  it('keeps the row count of a 10,000-row export exact', () => {
    const rows = Array.from({ length: 10_000 }, (_, index) =>
      entry({ id: `ae-${index}`, seq: index + 1 }),
    );

    const csv = toAuditCsv(rows);

    expect(lines(csv)).toHaveLength(10_002); // header + 10,000 rows + the trailing newline
    expect(csv.endsWith('\r\n')).toBe(true);
  });
});
