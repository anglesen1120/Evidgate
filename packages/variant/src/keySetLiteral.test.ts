import { describe, expect, it } from 'vitest';
import { readKeySet } from './keySetLiteral.testing.js';

describe('readKeySet', () => {
  it('reads entries written after the opening brace on the same line', () => {
    const text = 'const S: Record<string, true> = { a: true,\n  b: true,\n};\n';

    expect(readKeySet(text, 'S')).toEqual(['a', 'b']);
  });

  it('reads a whole one-line literal', () => {
    expect(readKeySet("const S: Record<string, true> = { a: true, 'b-c': true };", 'S')).toEqual([
      'a',
      'b-c',
    ]);
  });

  it('is not ended by a brace-leading line inside a block comment', () => {
    const text = [
      'const S: Record<string, true> = { a: true, /* retired:',
      '} was here',
      '  */',
      '  b: true,',
      '};',
    ].join('\n');

    expect(readKeySet(text, 'S')).toEqual(['a', 'b']);
  });

  it.each([
    'const S: Record<string, true> = { a: true',
    'const S: Record<string, true> = { a: true b: true };',
    'const S: Record<string, true> = { a: true, /* no close',
  ])('fails closed on malformed source %#', (text) => {
    expect(() => readKeySet(text, 'S')).toThrow(/syntax error/);
  });

  it('fails closed on an entry it does not understand', () => {
    const text = 'const S: Record<string, true> = {\n  a: true,\n  [k]: true,\n};\n';

    expect(() => readKeySet(text, 'S')).toThrow(/cannot read entry/);
  });

  it('fails closed when the literal is missing or has another shape', () => {
    expect(() => readKeySet('const T = 1;', 'S')).toThrow(/not found/);
    expect(() => readKeySet('const S: Record<string, true> = make();', 'S')).toThrow(/not `Record/);
    expect(() => readKeySet('const S: Record<string, true> = {};', 'S')).toThrow(/empty set/);
  });
});
