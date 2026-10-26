import { describe, expect, it } from 'vitest';

import { clipDiffText, MAX_DIFF_VALUE_CHARS } from './policy-diff';

const ELLIPSIS = '\u2026';

function endsInLoneHighSurrogate(text: string): boolean {
  const last = text.charCodeAt(text.length - 1);
  return last >= 0xd800 && last <= 0xdbff;
}

describe('clipDiffText boundaries (QA S86)', () => {
  it('returns an empty string unchanged', () => {
    expect(clipDiffText('')).toBe('');
  });

  it('returns a string of exactly the budget unchanged', () => {
    const text = 'a'.repeat(MAX_DIFF_VALUE_CHARS);
    expect(clipDiffText(text)).toBe(text);
  });

  it('cuts one character over the budget to exactly the budget, ending in an ellipsis', () => {
    const out = clipDiffText('a'.repeat(MAX_DIFF_VALUE_CHARS + 1));
    expect(out.length).toBe(MAX_DIFF_VALUE_CHARS);
    expect(out.endsWith(ELLIPSIS)).toBe(true);
    expect(out).toBe('a'.repeat(MAX_DIFF_VALUE_CHARS - 1) + ELLIPSIS);
  });

  it('never leaves a lone high surrogate when the cut lands inside a pair', () => {
    // index 198 is a high surrogate and 199 its low half; the cut keeps 0..198
    const text = 'a'.repeat(198) + '\uD83D\uDE00' + 'b'.repeat(50);
    const out = clipDiffText(text);
    expect(endsInLoneHighSurrogate(out.slice(0, -1))).toBe(false);
    expect(out).toBe('a'.repeat(198) + ELLIPSIS);
  });

  it('keeps a complete surrogate pair that fits inside the budget', () => {
    // pair occupies 197..198 and the kept prefix ends at 198: no cut inside it
    const text = 'a'.repeat(197) + '\uD83D\uDE00' + 'b'.repeat(50);
    const out = clipDiffText(text);
    expect(out.startsWith('a'.repeat(197) + '\uD83D\uDE00')).toBe(true);
    expect(out.endsWith(ELLIPSIS)).toBe(true);
  });

  it('bounds a 40 KiB single value (negative: no unbounded render)', () => {
    const out = clipDiffText('x'.repeat(40 * 1024));
    expect(out.length).toBe(MAX_DIFF_VALUE_CHARS);
  });
});
