import { describe, expect, it } from 'vitest';
import { parseLocalPreview, IMPORT_COLUMNS } from './local-file-preview';

const header = IMPORT_COLUMNS.join(',');

describe('local file preview (display only)', () => {
  it('keeps quoted CSV commas, doubled quotes, CRLF and embedded newlines in file order', () => {
    const rows = parseLocalPreview(
      `${header}\r\nnode,n1,,,module,"A, ""quoted""\nheading",,,,,,,\r\nitem,i1,n1,,,Next,,,,high,,,`,
      'csv',
    );
    expect(rows.rows.map((row) => row.index)).toEqual([1, 2]);
    expect(rows.rows[0]?.cells['title']).toBe('A, "quoted"\nheading');
    expect(rows.rows[1]?.cells['title']).toBe('Next');
  });

  it('reads JSON text without treating the file as schema validated or evaluating hostile text', () => {
    const text = '<img src=x onerror=alert(1)>';
    const result = parseLocalPreview(
      JSON.stringify({
        rows: [
          {
            kind: 'case',
            title: text,
            steps: { steps: [{ no: 1, action: '=1+1', expected: 'Literal' }] },
          },
        ],
      }),
      'json',
    );
    expect(result.rows[0]?.cells['title']).toBe(text);
    expect(result.rows[0]?.cells['steps']).toContain('=1+1');
    expect(result.issue).toBeNull();
  });

  it('keeps duplicate keys as separate 1-based rows without resolving their identity', () => {
    const result = parseLocalPreview('{"rows":[{"key":"same"},{"key":"same"}]}', 'json');
    expect(result.rows.map((row) => row.index)).toEqual([1, 2]);
  });

  it('returns an honest unavailable preview for malformed JSON, CSV quotes and an unknown header', () => {
    expect(parseLocalPreview('{', 'json').issue).toBe('unreadable');
    expect(parseLocalPreview(`${header}\n"unclosed`, 'csv').issue).toBe('unreadable');
    expect(parseLocalPreview('title\nHello', 'csv').issue).toBe('header');
  });

  it('retains blank data rows rather than silently dropping them', () => {
    expect(
      parseLocalPreview(`${header}\n\nnode,n1,,,module,Hello,,,,,,,`, 'csv').rows,
    ).toHaveLength(2);
  });

  it('bounds display at the contract row cap and labels omitted data without changing the upload', () => {
    const result = parseLocalPreview(
      JSON.stringify({ rows: Array.from({ length: 5001 }, () => ({ kind: 'node' })) }),
      'json',
    );
    expect(result.rows).toHaveLength(5000);
    expect(result.issue).toBe('display-cap');
  });
  it('shows XLSX rows without claiming to have parsed spreadsheet cell text', () => {
    expect(parseLocalPreview('binary workbook', 'xlsx')).toEqual({ rows: [], issue: 'xlsx' });
  });
  it('labels an over-cap CSV display without dropping the row that proves the cap was exceeded', () => {
    const result = parseLocalPreview(
      header +
        '\n' +
        Array.from({ length: 5001 }, () => 'node,n1,,,module,Title,,,,,,,0').join('\n'),
      'csv',
    );
    expect(result.rows).toHaveLength(5000);
    expect(result.issue).toBe('display-cap');
  });
});
