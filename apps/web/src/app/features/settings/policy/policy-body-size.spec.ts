// 09.7.1-T4 (plan 09.7.1 §2 AC4, S83 P4; review finding F1): the measurement a publish's size check
// rests on. Each case is a boundary the store depends on, so it is proved here once:
//
// - the bytes measured are the COMPACT wire body, not the pretty-printed textarea text — indentation
//   must never count against the cap;
// - 65 536 bytes are the cap and are accepted by the store, so the exported constant is the number
//   the contract names;
// - a multi-byte character counts as the bytes it occupies, because the cap is declared in bytes;
// - text that is not JSON has no wire body at all, which is `null`, never a made-up size.

import { POLICY_BODY_MAX_BYTES, policyBodyBytes } from './policy-body-size';

/** The UTF-8 bytes of a string, so a case can state the size it expects independently of the helper. */
function utf8Bytes(text: string): number {
  return new TextEncoder().encode(text).byteLength;
}

describe('policyBodyBytes (09.7.1-T4)', () => {
  it('names the contract’s 65 536-byte cap', () => {
    expect(POLICY_BODY_MAX_BYTES).toBe(65_536);
  });

  it('measures the compact body, not the pretty-printed text', () => {
    const compact = '{"flaky_budget":3,"critical_cases":["a","b"]}';
    const pretty = JSON.stringify(JSON.parse(compact), null, 2);

    expect(policyBodyBytes(pretty)).toBe(utf8Bytes(compact));
    // The indentation the editor shows is real text and would only inflate a text-based measurement.
    expect(utf8Bytes(pretty)).toBeGreaterThan(utf8Bytes(compact));
  });

  it('measures a body of exactly the cap as the cap, even when its text is far larger', () => {
    // `{"a":"` is six bytes and `"}` is two, so the filler carries the rest of the 65 536.
    const compact = `{"a":"${'x'.repeat(65_536 - 8)}"}`;
    const pretty = JSON.stringify(JSON.parse(compact), null, 2);

    expect(utf8Bytes(compact)).toBe(POLICY_BODY_MAX_BYTES);
    expect(policyBodyBytes(compact)).toBe(POLICY_BODY_MAX_BYTES);
    expect(utf8Bytes(pretty)).toBeGreaterThan(POLICY_BODY_MAX_BYTES);
    expect(policyBodyBytes(pretty)).toBe(POLICY_BODY_MAX_BYTES);
  });

  it('counts a multi-byte character as every byte it occupies', () => {
    // "ế" is three UTF-8 bytes and the emoji four, so the document is bytes, not characters: this
    // text is 14 UTF-16 units long and 18 bytes wide.
    const text = '{"note":"ế😀"}';

    expect(text.length).toBe(14);
    expect(policyBodyBytes(text)).toBe(18);
  });

  it('measures the compact form, so an escape the text spells out counts once', () => {
    // `\u0041` is six characters in the editor's text and one byte in the document it describes.
    const text = '{"note":"\\u0041"}';

    expect(text.length).toBe(17);
    expect(policyBodyBytes(text)).toBe(12);
    expect(policyBodyBytes(text)).toBe(utf8Bytes('{"note":"A"}'));
  });

  it('measures a body that is LARGER on the wire than in the text', () => {
    // The direction a cap must not miss: `1E5` is three characters and the document it serialises to
    // is six (`100000`). A text-based measurement would wave through a body the server refuses.
    const text = '{"n":1E5}';

    expect(text.length).toBe(9);
    expect(policyBodyBytes(text)).toBe(12);
    expect(policyBodyBytes(text)).toBeGreaterThan(text.length);
    expect(policyBodyBytes(text)).toBe(utf8Bytes('{"n":100000}'));
  });

  it.each([
    ['an empty string', ''],
    ['text that is not JSON', 'not json at all'],
    ['truncated JSON', '{"a":'],
  ])('answers no size at all for %s', (_label, text) => {
    expect(policyBodyBytes(text)).toBeNull();
  });

  it('measures a JSON scalar or array too, so the store can refuse its shape separately', () => {
    expect(policyBodyBytes('42')).toBe(2);
    expect(policyBodyBytes('[ 1, 2 ]')).toBe(utf8Bytes('[1,2]'));
  });
});
