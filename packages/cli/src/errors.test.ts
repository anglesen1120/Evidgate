import { describe, expect, it } from 'vitest';

import { escapeControlCharacters } from './errors.js';

// 04.2.2-T1 S3: the escape set covers the Unicode line and paragraph separators
// (Zl/Zp), so U+2028 and U+2029 cannot reach stderr as raw line breaks
// (contracts/cli.md §5).
describe('escapeControlCharacters', () => {
  it('escapes U+2028 and U+2029 as \\u2028 and \\u2029', () => {
    expect(escapeControlCharacters('a\u2028b\u2029c')).toBe('a\\u2028b\\u2029c');
  });
});
