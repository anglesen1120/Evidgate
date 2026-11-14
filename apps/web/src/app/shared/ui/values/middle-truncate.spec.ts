import { middleTruncate } from './middle-truncate';

describe('middleTruncate', () => {
  it.each(['', 'short-id', 'a'.repeat(32)])('keeps a complete short value: %j', (value) => {
    expect(middleTruncate(value)).toBe(value);
  });

  it('preserves both distinguishing ends of a long hash without mutating it', () => {
    const value = `sha256:${'0123456789abcdef'.repeat(4)}`;
    expect(middleTruncate(value)).toBe(`${value.slice(0, 16)}…${value.slice(-15)}`);
    expect(value).toHaveLength(71);
  });

  it('does not split Vietnamese combining marks or emoji in a branch preview', () => {
    const value = 'a\u0301b\u0301c\u0301d\u0301e\u0301f\u0301g\u0301h\u0301';
    expect(middleTruncate(value, 5)).toBe('a\u0301b\u0301…g\u0301h\u0301');
    expect(middleTruncate('a👩‍💻b👩‍💻c👩‍💻d', 5)).toBe('a👩‍💻…👩‍💻d');
  });

  it.each([0, -1, 2])('keeps two useful ends even with an undersized budget: %j', (budget) => {
    expect(middleTruncate('abcdef', budget)).toBe('a…f');
  });

  it('uses a safe default for non-finite budgets and floors fractional budgets', () => {
    expect(middleTruncate('abcdef', Number.NaN)).toBe('abcdef');
    expect(middleTruncate('abcdef', 4.9)).toBe('ab…f');
  });
});
