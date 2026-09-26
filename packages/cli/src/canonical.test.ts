import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  assertCanonicalRoundTrip,
  canonicalHash,
  canonicalHashOfCanonical,
  canonicalHashPrefix,
  canonicalMaxDepth,
  canonicalizeJson,
} from './canonical.js';

interface GoldenVector {
  readonly name: string;
  readonly description: string;
  readonly input: unknown;
  readonly canonical: string;
  readonly sha256: string;
}

// 01.3.2-T4.2 / AC5: fixtures/canonical/*.json is the contract shared with the
// xUnit suite (tests/Canonicalization.UnitTests/Canonicalization.GoldenVectorTests.cs)
// — the CLI and Evidgate.BuildingBlocks.Canonicalization must produce the same
// canonical bytes and therefore the same digest for every one of them.
const fixturesDirectory = resolve(
  dirname(fileURLToPath(import.meta.url)),
  '../../..',
  'fixtures',
  'canonical',
);
const goldenVectors = readdirSync(fixturesDirectory)
  .filter((file) => file.endsWith('.json'))
  .sort()
  .map((file) => ({
    file,
    vector: JSON.parse(readFileSync(join(fixturesDirectory, file), 'utf8')) as GoldenVector,
  }));

// 01.3.2-T4.1 / AC5: fixtures/rfc8785/{input,output}/*.json are the reference pairs
// published with cyberphone/json-canonicalization (Apache-2.0, see the README next to
// them). The xUnit suite asserts the same six files byte for byte
// (tests/Canonicalization.UnitTests/Rfc8785VectorTests.cs), so a regression on either
// side fails that side's suite.
const rfc8785Directory = resolve(
  dirname(fileURLToPath(import.meta.url)),
  '../../..',
  'fixtures',
  'rfc8785',
);
const rfc8785VectorNames = ['arrays', 'french', 'structures', 'unicode', 'values', 'weird'];

// RFC 8785 Appendix B ("ECMAScript-Compatible JSON Number Serialization
// Samples"): the IEEE 754 values a JCS serializer must reproduce, including the
// round-to-even case (1424953923781206.2) and the fixed/exponential switchovers.
const appendixBNumbers: ReadonlyArray<readonly [number, string]> = [
  [0, '0'],
  [-0, '0'],
  [Number.MIN_VALUE, '5e-324'],
  [-Number.MIN_VALUE, '-5e-324'],
  [Number.MAX_VALUE, '1.7976931348623157e+308'],
  [-Number.MAX_VALUE, '-1.7976931348623157e+308'],
  [9007199254740992, '9007199254740992'],
  [-9007199254740992, '-9007199254740992'],
  [2.9514790517935283e20, '295147905179352830000'],
  [9.999999999999997e22, '9.999999999999997e+22'],
  [1e23, '1e+23'],
  [1.0000000000000001e23, '1.0000000000000001e+23'],
  [9.999999999999997e20, '999999999999999700000'],
  [9.999999999999999e20, '999999999999999900000'],
  [1e21, '1e+21'],
  [1e-7, '1e-7'],
  [9.999999999999997e-7, '9.999999999999997e-7'],
  [0.000001, '0.000001'],
  [333333333.3333332, '333333333.3333332'],
  [333333333.33333325, '333333333.33333325'],
  [333333333.3333333, '333333333.3333333'],
  [333333333.3333334, '333333333.3333334'],
  [333333333.33333343, '333333333.33333343'],
  [-0.0000033333333333333333, '-0.0000033333333333333333'],
  [1424953923781206.2, '1424953923781206.2'],
];

describe('canonicalizeJson (RFC 8785 JCS)', () => {
  it('serializes numbers as ECMAScript does (RFC 8785 Appendix B)', () => {
    for (const [value, expected] of appendixBNumbers) {
      expect(canonicalizeJson([value]), `number ${expected}`).toBe(`[${expected}]`);
    }
  });

  it('escapes only the characters §3.2.2.2 requires, in lowercase for \\u', () => {
    // \u007f (DEL) and \u0080 (C1) are outside the escaped range; only U+0000–U+001F
    // is, and the predefined set uses \b \t \n \f \r.
    expect(canonicalizeJson({ a: '\u0000\u0007\u000b\u001f\u007f\u0080' })).toBe(
      '{"a":"\\u0000\\u0007\\u000b\\u001f\u007f\u0080"}',
    );
    expect(canonicalizeJson({ a: '\b\t\n\f\r"\\/' })).toBe('{"a":"\\b\\t\\n\\f\\r\\"\\\\/"}');
  });

  it('sorts properties by UTF-16 code unit, ignoring locale (RFC 8785 §3.2.3)', () => {
    // The RFC 3.2.3 sample, with every name written as a code point: the last three
    // are the interesting ones — U+FB33 (64307) after the U+1F60A surrogate pair
    // (55357) even though it precedes it by code point.
    expect(
      canonicalizeJson({
        '\u20ac': 'Euro Sign',
        '\r': 'Carriage Return',
        '\n': 'Newline',
        '1': 'One',
        '\u0080': 'Control\u007f',
        '\ud83d\ude0a': 'Smiley',
        '\u00f6': 'Latin Small Letter O With Diaeresis',
        '\ufb33': 'Hebrew Letter Dalet With Dagesh',
        '</script>': 'Browser Challenge',
      }),
    ).toBe(
      '{"\\n":"Newline","\\r":"Carriage Return","1":"One","</script>":"Browser Challenge",' +
        '"\u0080":"Control\u007f","\u00f6":"Latin Small Letter O With Diaeresis",' +
        '"\u20ac":"Euro Sign","\ud83d\ude0a":"Smiley","\ufb33":"Hebrew Letter Dalet With Dagesh"}',
    );

    expect(
      canonicalizeJson({
        'p\u00e9ch\u00e9': 'is wrong according to French',
        'p\u00eache': 'but canonicalization MUST',
        peach: 'This sorting order',
        sin: 'ignore locale',
      }),
    ).toBe(
      '{"peach":"This sorting order","p\u00e9ch\u00e9":"is wrong according to French",' +
        '"p\u00eache":"but canonicalization MUST","sin":"ignore locale"}',
    );
  });

  it('sorts objects inside arrays but never reorders the array', () => {
    expect(canonicalizeJson([56, { d: true, '10': null, '1': [] }])).toBe(
      '[56,{"1":[],"10":null,"d":true}]',
    );
  });

  it('rejects values JSON cannot represent instead of emitting null', () => {
    expect(() => canonicalizeJson(undefined)).toThrow(TypeError);
    expect(() => canonicalizeJson({ a: Number.NaN })).toThrow('NaN is not allowed');
    expect(() => canonicalizeJson({ a: Number.POSITIVE_INFINITY })).toThrow(
      'Infinity is not allowed',
    );
    expect(() => canonicalizeJson({ a: '\ud800' })).toThrow('Lone surrogate');
  });

  it('rejects a circular value instead of hanging', () => {
    const cyclic: { self?: unknown } = {};
    cyclic.self = cyclic;
    expect(() => canonicalizeJson(cyclic)).toThrow('Circular reference');
  });
});

describe('canonicalHash (00 §7)', () => {
  it('is sha256 of the canonical UTF-8 bytes, in lowercase hex', () => {
    // Independent oracle: printf '{"a":1}' | sha256sum
    const independentDigest = '015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862';

    expect(canonicalHash({ a: 1 })).toBe(`${canonicalHashPrefix}${independentDigest}`);
    expect(canonicalHashOfCanonical('{"a":1}')).toBe(`${canonicalHashPrefix}${independentDigest}`);
    expect(canonicalHash({ a: 1 })).toMatch(/^sha256:[0-9a-f]{64}$/);
  });

  it('is independent of property order and whitespace in the input', () => {
    expect(canonicalHash({ b: [2], a: 1 })).toBe(canonicalHash({ a: 1, b: [2] }));
  });

  it('produces the shared golden vectors', () => {
    expect(goldenVectors.map(({ vector }) => vector.name).sort()).toEqual([
      'candidate',
      'evidence',
      'manifest',
      'policy',
    ]);

    for (const { file, vector } of goldenVectors) {
      expect(canonicalizeJson(vector.input), `${file} canonical text`).toBe(vector.canonical);
      expect(canonicalHash(vector.input), `${file} digest`).toBe(vector.sha256);
      expect(canonicalHashOfCanonical(vector.canonical), `${file} recorded digest`).toBe(
        vector.sha256,
      );
    }
  });
});

// 01.3.2-T4.1 / AC5: the reference documents themselves, not only the RFC 8785 rules
// they exercise — the six pairs the xUnit suite reads from the same directory.
describe('RFC 8785 reference vectors (01.3.2-T4.1)', () => {
  it('canonicalizes every vendored input/output pair byte for byte', () => {
    const files = readdirSync(join(rfc8785Directory, 'input'))
      .filter((file) => file.endsWith('.json'))
      .sort();

    expect(files).toEqual(rfc8785VectorNames.map((name) => `${name}.json`));

    for (const name of rfc8785VectorNames) {
      const input = JSON.parse(
        readFileSync(join(rfc8785Directory, 'input', `${name}.json`), 'utf8'),
      ) as unknown;
      // `output/<name>.json` *is* the canonical byte sequence — compact, no trailing
      // newline — so it is compared verbatim, never re-formatted or re-parsed.
      const expected = readFileSync(join(rfc8785Directory, 'output', `${name}.json`), 'utf8');

      expect(canonicalizeJson(input), `${name} canonical text`).toBe(expected);
      expect(canonicalHash(input), `${name} digest`).toBe(canonicalHashOfCanonical(expected));
      expect(canonicalizeJson(JSON.parse(expected)), `${name} is a fixed point`).toBe(expected);
    }
  });
});

// 01.3.2-T4.2 / AC5: the adversarial half of the cross-runtime contract. The identical
// table is asserted by tests/Canonicalization.UnitTests/CrossRuntimeParityVectorTests.cs,
// so a divergence in either runtime fails that runtime's suite. Expected canonical text
// follows RFC 8785 §3.2.2/§3.2.3; every digest was re-derived from the expected bytes
// with an independent SHA-256 implementation (python3 hashlib), not from this wrapper.
const parityVectors: ReadonlyArray<readonly [string, string, string]> = [
  // Numbers: -0, the fixed/exponential switchovers, subnormals, overflow to zero,
  // integers wider than a double, and literal forms that normalize away.
  [
    '[-0,0,-0.0,0.0,1.0,1E2,1e+21]',
    '[0,0,0,0,1,100,1e+21]',
    'sha256:1bea963f1afa945ca4f8335ec394c79f38127ed7dc4083e80e3cca5e9c7bd801',
  ],
  [
    '[0.30000000000000004,0.1,0.2]',
    '[0.30000000000000004,0.1,0.2]',
    'sha256:7716063af38cf70cb04545912c972f2c1ae47dcc11110a3db80744387f2734ca',
  ],
  [
    '[9007199254740993,1000000000000000128]',
    '[9007199254740992,1000000000000000100]',
    'sha256:394cbca831c10541ba39a7d997927f68632546465cf602ed21d8ec862cb43391',
  ],
  [
    '[1e-400,-1e-400,5e-324]',
    '[0,0,5e-324]',
    'sha256:346d05c70998e776771fbb847896d9d452b3eb68d42f21cf6dde2ed928297af7',
  ],
  [
    '[12345678901234567890123456789012345678901234567890]',
    '[1.2345678901234567e+49]',
    'sha256:d9a7c7290b6bcfc4cd372956b8967d73a6263c962d473f6c7e873aa32f245ad9',
  ],
  [
    '[2.2250738585072011e-308,2.2250738585072014e-308]',
    '[2.225073858507201e-308,2.2250738585072014e-308]',
    'sha256:a0c21e1959079b96ee74847b069bdf895d5153d6752dde4c87802756e50ee3f2',
  ],
  [
    '[1.7976931348623157e308,1.7976931348623158e308]',
    '[1.7976931348623157e+308,1.7976931348623157e+308]',
    'sha256:5f3591f12a80bc6f4b84bc0accbc6f5e037bd9fb1b2ea2f76328b3d9321545b1',
  ],
  [
    '[1e21,1e20,1e22,1e-6,1e-7,0.000001,0.0000001]',
    '[1e+21,100000000000000000000,1e+22,0.000001,1e-7,0.000001,1e-7]',
    'sha256:f69dd5f809bc6e321a24d1528ceb5a5967ec1b0e64e2d81ae9345023b05946ce',
  ],

  // Strings: U+2028/U+2029, U+007F and U+009F stay raw; the predefined set and the
  // remaining C0 controls are escaped; \/ and \u0041 normalize away.
  [
    '["\\u2028\\u2029\\u007f\\u009f\\ud83d\\ude00"]',
    '["\u2028\u2029\u007f\u009f\ud83d\ude00"]',
    'sha256:975ce8644d87705a349d5198324b7cb30fce66baf2d983226af3a3b020defa63',
  ],
  [
    '["\\u0041\\/"]',
    '["A/"]',
    'sha256:1d39b7979d7f552f16057c3ac947e0a80a7fc19cd75e905635f03cdb2e19edb0',
  ],
  [
    '["\\b\\t\\n\\f\\r\\"\\\\"]',
    '["\\b\\t\\n\\f\\r\\"\\\\"]',
    'sha256:d4277361ed3d96172703b0b72063e0e59ffb5f63a59c56d42731721618421dd9',
  ],
  [
    '["\\u0000\\u000b\\u001f"]',
    '["\\u0000\\u000b\\u001f"]',
    'sha256:3f8af4e1c1c61e3cd7e32826517e5b188d32b05035e8f2c85f314957de695732',
  ],

  // Empty containers, and member order: UTF-16 code units, so an astral pair (0xD83D…)
  // sorts before U+FB33 but after "1", and "" sorts first.
  ['{}', '{}', 'sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a'],
  ['[]', '[]', 'sha256:4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'],
  ['{"":""}', '{"":""}', 'sha256:86cfeee6df382a203f626305afbca44d228cac67acd151ccf2db29530c548fea'],
  [
    '{"a":{}}',
    '{"a":{}}',
    'sha256:5d8171b9fc362e385a79ccd9d7992c96bcb4afca51d6278068bd1df49863b3a7',
  ],
  [
    '{"\\ufb33":1,"\\ud83d\\ude00":2,"\\r":3,"1":4,"":5}',
    '{"":5,"\\r":3,"1":4,"\ud83d\ude00":2,"\ufb33":1}',
    'sha256:718ebb063d8f2770d90a279a7ee1703f1765a3489824e1f76f8ba91841ef7695',
  ],
  [
    '{"b":[2,{"d":true,"10":null,"1":[]}],"a":1}',
    '{"a":1,"b":[2,{"1":[],"10":null,"d":true}]}',
    'sha256:555fd3aa5a43ca692a82bacdb049d11c81947130609e419a457ae37f5820911c',
  ],
  [
    '{"\\ue000":1,"\\ud800\\udc00":2}',
    '{"\ud800\udc00":2,"\ue000":1}',
    'sha256:9d4cdc71dda603c42f9b21d88d0c2ffc31a76cd1bd461d7359406cf169845f1e',
  ],
];

describe('cross-runtime parity vectors (01.3.2-T4.2)', () => {
  it('canonicalizes and hashes the shared adversarial vectors', () => {
    for (const [input, canonical, digest] of parityVectors) {
      const parsed: unknown = JSON.parse(input);

      expect(canonicalizeJson(parsed), `${input} canonical text`).toBe(canonical);
      expect(canonicalHash(parsed), `${input} digest`).toBe(digest);
      expect(canonicalHashOfCanonical(canonical), `${input} canonical digest`).toBe(digest);
    }
  });

  // DEF-3 / 00 §5.7 S5: the CLI applies the same depth rule as the server's reader, so
  // both runtimes agree on the verdict. The xUnit twin is
  // CrossRuntimeParityVectorTests.Depth_Cap_Is_Exact (accept 64, reject 65).
  it('caps container nesting at 64 levels, exactly as the server does', () => {
    expect(canonicalMaxDepth).toBe(64);

    const arraysAtCap = '['.repeat(canonicalMaxDepth) + ']'.repeat(canonicalMaxDepth);
    expect(canonicalizeJson(JSON.parse(arraysAtCap))).toBe(arraysAtCap);
    // The digest the xUnit suite asserts for the same document: parity at the cap.
    expect(canonicalHash(JSON.parse(arraysAtCap))).toBe(
      'sha256:b3ff3b51ce17ef2a2a68203329ff0e73738c07df874044149c283b28841a0035',
    );

    const objectsAtCap = '{"a":'.repeat(canonicalMaxDepth) + '1' + '}'.repeat(canonicalMaxDepth);
    expect(canonicalizeJson(JSON.parse(objectsAtCap))).toBe(objectsAtCap);

    // One container deeper than the server accepts: rejected, not hashed (parity at 65).
    expect(() => canonicalizeJson(JSON.parse(`[${arraysAtCap}]`))).toThrow(RangeError);
    expect(() => canonicalizeJson(JSON.parse('{"a":'.repeat(65) + '1' + '}'.repeat(65)))).toThrow(
      RangeError,
    );
  });

  // The depth walk runs before `canonicalize`, and its cap cannot reject a DAG (a
  // reference shared by several parents is not a cycle), so measuring a shared subtree
  // once per path would walk 2^40 containers here and time out before `canonicalize` ever
  // saw the value. Each shared subtree is memoised instead.
  it('walks a shared-reference DAG once instead of once per path', () => {
    const root: Record<string, unknown> = {};
    let deepest = root;
    for (let level = 0; level < 40; level++) {
      const child: Record<string, unknown> = {};
      deepest.left = child;
      deepest.right = child; // one node, two parents: 2^40 paths, 41 containers
      deepest = child;
    }
    deepest.back = root; // closes the cycle, the only thing `canonicalize` can reject

    expect(() => canonicalizeJson(root)).toThrow('Circular reference');
  });

  // The memo is not a licence to skip the cap: a shared subtree reached again at a deeper
  // level is re-checked against it, which is the only way a DAG can exceed 64 levels —
  // `canonicalize` accepts the deep path, because the shallow one already released the
  // shared containers from its `seen` set.
  it('still rejects a shared subtree that only exceeds the cap on its deep path', () => {
    const chainOf = (containers: number): Record<string, unknown> => {
      const head: Record<string, unknown> = {};
      let node = head;
      for (let index = 1; index < containers; index++) {
        const child: Record<string, unknown> = {};
        node.next = child;
        node = child;
      }
      return head;
    };

    const shared = chainOf(canonicalMaxDepth - 1); // alone: containers at levels 1..63
    expect(canonicalizeJson(shared)).toBeTypeOf('string');

    // `aa` puts the shared subtree at level 2 (deepest container 64, accepted). The walk
    // follows insertion order, so it measures the subtree under `aa` first; `zz`'s level-3
    // reference to the same subtree is then a memo hit whose deepest container would sit
    // at 65.
    expect(() => canonicalizeJson({ aa: shared, zz: { inner: shared } })).toThrow(RangeError);
  });

  // DEF-1: Node 26.10.0 can mis-decode an escaped member name once a process is warm, so
  // `canonicalizeJson` re-checks that its own output survives a parse. The corruption
  // itself is process-history dependent (a small document is stable over 200,000 parses on
  // this host), so the *firing* path is pinned with a text that genuinely does not
  // round-trip; the macro repro is recorded in the feature plan §9.
  it('withholds a digest when the runtime cannot reproduce its own canonical text', () => {
    expect(canonicalizeJson({ a: 1 })).toBe('{"a":1}');
    expect(() => assertCanonicalRoundTrip('{"a":1}')).not.toThrow();
    // Not canonical: a second canonicalisation reorders the members, so the text diverges.
    expect(() => assertCanonicalRoundTrip('{"b":2,"a":1}')).toThrow(/self-consistent/);
  });

  // The member names the runtime defect corrupts (QA corpus cases rand431/rand1368 used
  // exactly these escapes): the RFC-correct decode, which the server also produces.
  it('decodes escaped member names the way the server does', () => {
    expect(canonicalizeJson(JSON.parse('{"\\r":1,"\\f":2,"\\u0007":3}'))).toBe(
      '{"\\u0007":3,"\\f":2,"\\r":1}',
    );
    expect(canonicalHash(JSON.parse('{"\\r":1,"\\f":2,"\\u0007":3}'))).toBe(
      canonicalHashOfCanonical('{"\\u0007":3,"\\f":2,"\\r":1}'),
    );
  });
});
