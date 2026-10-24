// 09.1.1-T3 review F15: `crypto.randomUUID` is absent in a non-secure context (plain http outside
// localhost), so `newUuid` is the app's only UUID source. It must emit an RFC 4122 v4 UUID from
// either Web Crypto member and refuse rather than degrade to a predictable fallback.
import { newUuid } from './random-id';

const UUID_V4 = /^([0-9a-f]{8})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{12})$/;

/** Captured before any test replaces the global, so the fallback can delegate to the real one. */
const realCrypto = globalThis.crypto;
const cryptoDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'crypto');

/** Swap the whole Web Crypto object, the way a non-secure context or a legacy engine differs. */
function replaceCrypto(value: unknown): void {
  Object.defineProperty(globalThis, 'crypto', {
    configurable: true,
    enumerable: true,
    writable: true,
    value,
  });
}

describe('newUuid', () => {
  afterEach(() => {
    if (cryptoDescriptor === undefined) {
      Reflect.deleteProperty(globalThis, 'crypto');
      return;
    }
    Object.defineProperty(globalThis, 'crypto', cryptoDescriptor);
  });

  it('prefers crypto.randomUUID and never reads getRandomValues when it exists', () => {
    replaceCrypto({
      randomUUID: () => '11111111-2222-4333-8444-555555555555',
      getRandomValues: () => {
        throw new Error('getRandomValues must not be used while randomUUID exists');
      },
    });

    expect(newUuid()).toBe('11111111-2222-4333-8444-555555555555');
  });

  it('builds a lower-case v4 UUID from crypto.getRandomValues without crypto.randomUUID', () => {
    replaceCrypto({
      getRandomValues: (bytes: Uint8Array<ArrayBuffer>) => realCrypto.getRandomValues(bytes),
    });

    const first = newUuid();
    const second = newUuid();

    expect(first).toMatch(UUID_V4);
    expect(second).toMatch(UUID_V4);
    expect(first).not.toBe(second);
    const groups = UUID_V4.exec(first);
    expect(groups?.[3]?.charAt(0)).toBe('4');
    expect(groups?.[4]?.charAt(0) ?? '').toMatch(/[89ab]/);
  });

  it('sets the version and variant bits of an all-zero random source', () => {
    replaceCrypto({ getRandomValues: (bytes: Uint8Array) => bytes });

    expect(newUuid()).toBe('00000000-0000-4000-8000-000000000000');
  });

  it('keeps the low six bits of the variant byte and overwrites only the version byte', () => {
    replaceCrypto({ getRandomValues: (bytes: Uint8Array) => bytes.fill(0xff) });

    expect(newUuid()).toBe('ffffffff-ffff-4fff-bfff-ffffffffffff');
  });

  it('emits 200 distinct v4 UUIDs from the real random source', () => {
    replaceCrypto({
      getRandomValues: (bytes: Uint8Array<ArrayBuffer>) => realCrypto.getRandomValues(bytes),
    });

    const ids = new Set(Array.from({ length: 200 }, () => newUuid()));

    expect(ids.size).toBe(200);
    for (const id of ids) {
      expect(id).toMatch(UUID_V4);
    }
  });

  it('throws a clear error when neither Web Crypto member exists', () => {
    replaceCrypto({});

    expect(() => newUuid()).toThrow(/neither crypto\.randomUUID nor crypto\.getRandomValues/);
  });
});
