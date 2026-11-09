import {
  REASON_CODE_POINT_LIMIT,
  REQUEST_BODY_BYTE_LIMIT,
  reasonExceedsLimit,
  serializedBodyBytes,
} from './reason-limit';

const PROJECT_ID = '0b1f0e2a-1111-4222-8333-444455556666';
type Scope = Parameters<typeof reasonExceedsLimit>[1];

/** The body the store actually sends, built here so the boundary is not taken from the helper. */
function bodyBytes(reason: string, scope: Scope): number {
  const body =
    scope === 'project'
      ? { scope: 'project', project_id: PROJECT_ID, mode: 'disabled', reason }
      : { scope: 'tenant', mode: 'disabled', reason };
  return new TextEncoder().encode(JSON.stringify(body)).length;
}

describe('reasonExceedsLimit', () => {
  it('accepts 2000 ASCII code points and refuses 2001', () => {
    expect(reasonExceedsLimit('a'.repeat(2000), 'tenant', PROJECT_ID)).toBe(false);
    expect(reasonExceedsLimit('a'.repeat(2001), 'tenant', PROJECT_ID)).toBe(true);
  });

  it('trims trailing whitespace before measuring either limit', () => {
    expect(reasonExceedsLimit(`${'a'.repeat(2000)}   `, 'tenant', PROJECT_ID)).toBe(false);
    expect(reasonExceedsLimit(`${'a'.repeat(2000)} b`, 'tenant', PROJECT_ID)).toBe(true);
  });

  it('stops at the byte cap exactly: 1012 four-byte emoji pass, 1013 do not', () => {
    const atCap = '🙂'.repeat(1012);
    const overCap = '🙂'.repeat(1013);

    expect([...atCap].length).toBeLessThanOrEqual(REASON_CODE_POINT_LIMIT);
    expect(bodyBytes(atCap, 'tenant')).toBe(REQUEST_BODY_BYTE_LIMIT);
    expect(reasonExceedsLimit(atCap, 'tenant', PROJECT_ID)).toBe(false);
    expect(bodyBytes(overCap, 'tenant')).toBe(REQUEST_BODY_BYTE_LIMIT + 4);
    expect(reasonExceedsLimit(overCap, 'tenant', PROJECT_ID)).toBe(true);
  });

  it('refuses the body one byte past the cap', () => {
    const atCap = '🙂'.repeat(1012);
    const oneByteOver = `${atCap}a`;

    expect([...oneByteOver].length).toBeLessThanOrEqual(REASON_CODE_POINT_LIMIT);
    expect(bodyBytes(oneByteOver, 'tenant')).toBe(REQUEST_BODY_BYTE_LIMIT + 1);
    expect(reasonExceedsLimit(oneByteOver, 'tenant', PROJECT_ID)).toBe(true);
  });

  it('counts the project envelope, so the project boundary is two emoji lower', () => {
    expect(reasonExceedsLimit('🙂'.repeat(998), 'project', PROJECT_ID)).toBe(false);
    expect(reasonExceedsLimit('🙂'.repeat(999), 'project', PROJECT_ID)).toBe(true);
    expect(bodyBytes('🙂'.repeat(999), 'project')).toBeGreaterThan(REQUEST_BODY_BYTE_LIMIT);
  });

  it('refuses three-byte CJK inside the code-point limit when the bytes overflow', () => {
    expect([...'中'.repeat(1332)].length).toBeLessThan(REASON_CODE_POINT_LIMIT);
    expect(reasonExceedsLimit('中'.repeat(1331), 'project', PROJECT_ID)).toBe(false);
    expect(reasonExceedsLimit('中'.repeat(1332), 'project', PROJECT_ID)).toBe(true);
  });

  it('counts JSON escaping: quotes cost two bytes each after serialization', () => {
    expect([...'"'.repeat(1998)].length).toBeLessThan(REASON_CODE_POINT_LIMIT);
    expect(bodyBytes('"'.repeat(1997), 'project')).toBeLessThanOrEqual(REQUEST_BODY_BYTE_LIMIT);
    expect(reasonExceedsLimit('"'.repeat(1997), 'project', PROJECT_ID)).toBe(false);
    expect(reasonExceedsLimit('"'.repeat(1998), 'project', PROJECT_ID)).toBe(true);
  });

  it('reports the serialized size of both body shapes', () => {
    expect(serializedBodyBytes('abc', 'tenant', PROJECT_ID)).toBe(bodyBytes('abc', 'tenant'));
    expect(serializedBodyBytes('abc', 'project', PROJECT_ID)).toBe(bodyBytes('abc', 'project'));
  });

  it('escapes a lone surrogate instead of miscounting it as one three-byte character', () => {
    // JSON.stringify writes a lone surrogate as `\udXXX`, six bytes, which is what the wire sees.
    const lone = '\ud800';
    expect(bodyBytes(lone, 'tenant')).toBe(serializedBodyBytes(lone, 'tenant', PROJECT_ID));
    expect(reasonExceedsLimit(lone.repeat(700), 'tenant', PROJECT_ID)).toBe(true);
  });
});
