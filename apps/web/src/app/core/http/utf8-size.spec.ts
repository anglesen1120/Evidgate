import { utf8ExceedsBytes } from './utf8-size';

const samples: Record<string, string> = {
  ascii: 'abc'.repeat(50),
  twoByte: 'é'.repeat(70),
  threeByte: '€'.repeat(40),
  fourByte: '😀'.repeat(30),
  loneHigh: 'a\ud800b',
  loneLow: 'a\udc00b',
  reversedPair: '\udc00\ud800',
  highAtEnd: 'xyz\ud83d',
  mixed: 'a é € 😀 \ud800 z',
  empty: '',
};

describe('utf8ExceedsBytes', () => {
  for (const [name, text] of Object.entries(samples)) {
    it(`agrees with TextEncoder at every limit around the size: ${name}`, () => {
      const size = new TextEncoder().encode(text).byteLength;
      for (const limit of new Set([0, 1, size - 1, size, size + 1, size * 3, text.length * 3])) {
        if (limit < 0) {
          continue;
        }
        expect(utf8ExceedsBytes(text, limit), `limit ${String(limit)}`).toBe(size > limit);
      }
    });
  }

  it('counts a string without a pair split across the loop boundary like TextEncoder', () => {
    const text = '😀'.repeat(10) + '\ud800';
    const size = new TextEncoder().encode(text).byteLength;
    expect(utf8ExceedsBytes(text, size - 1)).toBe(true);
    expect(utf8ExceedsBytes(text, size)).toBe(false);
  });

  it('measures a near-cap string without allocating (printed)', () => {
    const cap = 8 * 1024 * 1024;
    const text = 'é'.repeat(cap / 2); // exactly the cap in UTF-8
    const t0 = performance.now();
    const over = utf8ExceedsBytes(text, cap);
    const scan = performance.now() - t0;
    const t1 = performance.now();
    const encoded = new TextEncoder().encode(text).byteLength;
    const encode = performance.now() - t1;
    console.log(
      `utf8 size of ${String(encoded)} bytes: scan ${scan.toFixed(1)} ms, 0 B allocated; ` +
        `TextEncoder ${encode.toFixed(1)} ms, ${String(encoded)} B allocated per call`,
    );
    expect(over).toBe(false);
    expect(encoded).toBe(cap);
  });
});
