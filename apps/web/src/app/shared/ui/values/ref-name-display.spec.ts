// Format characters are a spoofing surface; ordinary Unicode must remain byte-identical.

import { escapeFormatChars, escapeFormatLabel, hasFormatChars } from './ref-name-display';

describe('escapeFormatChars', () => {
  it('leaves a plain ref name byte-identical', () => {
    expect(escapeFormatChars('refs/heads/feature/payments')).toBe('refs/heads/feature/payments');
  });

  it('returns an empty string for a run with no ref', () => {
    expect(escapeFormatChars('')).toBe('');
  });

  it('replaces a right-to-left override with a visible token', () => {
    // The classic spoof: a name that renders as `feature/exe.txt` but is stored with the override.
    expect(escapeFormatChars('feature/\u202Eexe.txt')).toBe('feature/⟨U+202E⟩exe.txt');
  });

  it.each([
    ['\u202A', 'U+202A'],
    ['\u202B', 'U+202B'],
    ['\u202C', 'U+202C'],
    ['\u202D', 'U+202D'],
    ['\u202E', 'U+202E'],
    ['\u2066', 'U+2066'],
    ['\u2067', 'U+2067'],
    ['\u2068', 'U+2068'],
    ['\u2069', 'U+2069'],
    ['\u200F', 'U+200F'],
    ['\u200E', 'U+200E'],
  ])('escapes the bidi %j as %s', (character, token) => {
    expect(escapeFormatChars(`main${character}gnp.exe`)).toBe(`main⟨${token}⟩gnp.exe`);
  });

  it('escapes every format character of the category, not just the first', () => {
    expect(escapeFormatChars('\u202Erefs\u2066/heads/\u202Dmain')).toBe(
      '⟨U+202E⟩refs⟨U+2066⟩/heads/⟨U+202D⟩main',
    );
  });

  it('escapes the zero-width joiner and the zero-width non-joiner', () => {
    expect(escapeFormatChars('a\u200Db\u200Cc')).toBe('a⟨U+200D⟩b⟨U+200C⟩c');
  });

  it('escapes an astral format character as one code point', () => {
    // U+E0001 LANGUAGE TAG: a supplementary-plane `Cf`, so a surrogate-pair split would print two
    // tokens for one character (or a lone surrogate).
    expect(escapeFormatChars('tag\u{E0001}name')).toBe('tag⟨U+E0001⟩name');
  });

  it('does not escape an emoji or a combining mark; a zero-width space is Cf and is escaped', () => {
    expect(escapeFormatChars('🚀/v1')).toBe('🚀/v1');
    expect(escapeFormatChars('e\u0301')).toBe('e\u0301');
    expect(escapeFormatChars('a\u200Bb')).toBe('a⟨U+200B⟩b');
  });

  it('produces a value whose token cannot be mistaken for the original character', () => {
    const escaped = escapeFormatChars('\u202E');
    expect(escaped).not.toContain('\u202E');
    expect(escaped).toBe('⟨U+202E⟩');
  });

  // A run row's rerun/reprocess labels are caller-written too, so the table escapes them with the
  // alias below. It must stay the same function — a copy would drift from the cases above.
  it('exposes the neutral label alias as the same escaping', () => {
    expect(escapeFormatLabel).toBe(escapeFormatChars);
    expect(escapeFormatLabel('failed\u202Esubset')).toBe('failed⟨U+202E⟩subset');
    expect(escapeFormatLabel('plain_label')).toBe('plain_label');
  });
});

describe('hasFormatChars', () => {
  it.each(['\u202E', '\u200B', '\u200C', '\u200D', '\u{E0001}'])(
    'detects format character %j consistently across repeated calls',
    (character) => {
      const value = `feature/${character}release`;
      expect(hasFormatChars(value)).toBe(true);
      expect(hasFormatChars(value)).toBe(true);
    },
  );

  it.each(['', 'refs/heads/main', '🚀/v1', 'e\u0301', 'feature/إصلاح'])(
    'does not flag ordinary Unicode %j',
    (value) => {
      expect(hasFormatChars(value)).toBe(false);
    },
  );
});
