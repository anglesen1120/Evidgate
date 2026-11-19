// The final body of a binary call: one size rule for the streaming check and the adapter, failing
// closed on a body whose size cannot be measured.
import { HttpHeaders, HttpResponse } from '@angular/common/http';
import { runInNewContext } from 'node:vm';

import { BINARY_BODY_LIMIT_BYTES } from './binary-transport';
import { exceedsBinaryCap, finalBodySize, toBinaryResponse } from './http-client-fetch-binary';

const OVER = BINARY_BODY_LIMIT_BYTES + 1;

function final(body: unknown): HttpResponse<unknown> {
  return new HttpResponse({ status: 200, body });
}

describe('final body size', () => {
  it('fails closed on a body that is not a buffer, view, Blob or string', () => {
    expect(exceedsBinaryCap(final({ byteLength: 1 }))).toBe(true);
    expect(exceedsBinaryCap(final(42))).toBe(true);
    expect(exceedsBinaryCap(final([1, 2, 3]))).toBe(true);
  });

  it('measures a Blob exactly', () => {
    expect(exceedsBinaryCap(final(new Blob(['abc'])))).toBe(false);
    expect(finalBodySize(new Blob(['abc']))).toBe(3);
    expect(finalBodySize({ size: 1, byteLength: 1 })).toBe(Infinity);
  });

  it('measures typed-array and DataView views and a buffer of another realm', () => {
    expect(finalBodySize(new Uint8Array(5))).toBe(5);
    expect(finalBodySize(new Uint16Array(5))).toBe(10);
    expect(finalBodySize(new DataView(new ArrayBuffer(7)))).toBe(7);
    const foreign = runInNewContext('new ArrayBuffer(9)') as unknown;
    expect(foreign instanceof ArrayBuffer).toBe(false);
    expect(finalBodySize(foreign)).toBe(9);
    expect(exceedsBinaryCap(final(runInNewContext(`new ArrayBuffer(${String(OVER)})`)))).toBe(true);
  });

  it('fails closed on a proxy around an ArrayBuffer', () => {
    expect(finalBodySize(new Proxy(new ArrayBuffer(8), {}))).toBe(Infinity);
  });

  it('fails closed on a forged Uint8Array tag and byteLength', () => {
    const forged = { [Symbol.toStringTag]: 'Uint8Array', byteLength: 8 };
    expect(finalBodySize(forged)).toBe(Infinity);
  });

  describe.runIf(typeof globalThis.SharedArrayBuffer === 'function')('shared memory', () => {
    it('fails closed on a SharedArrayBuffer', () => {
      expect(finalBodySize(new SharedArrayBuffer(8))).toBe(Infinity);
    });
  });

  it('keeps an exactly-cap ASCII string within the cap and refuses one byte more', () => {
    expect(exceedsBinaryCap(final('a'.repeat(BINARY_BODY_LIMIT_BYTES)))).toBe(false);
    expect(exceedsBinaryCap(final('a'.repeat(OVER)))).toBe(true);
  });

  it('refuses a string with fewer UTF-16 units than the cap but too many UTF-8 bytes', () => {
    const text = 'é'.repeat(BINARY_BODY_LIMIT_BYTES / 2 + 1);
    expect(text.length).toBeLessThan(BINARY_BODY_LIMIT_BYTES);
    expect(exceedsBinaryCap(final(text))).toBe(true);
  });

  it('keeps null and the within-cap forms inside the cap', () => {
    expect(exceedsBinaryCap(final(null))).toBe(false);
    expect(exceedsBinaryCap(final(new ArrayBuffer(8)))).toBe(false);
    expect(exceedsBinaryCap(final('text'))).toBe(false);
  });

  it('returns a Response for an exactly-cap view, Blob and ArrayBuffer', () => {
    const headers = new HttpHeaders();
    const source = final(null);
    const within = new Uint8Array(BINARY_BODY_LIMIT_BYTES);
    expect(toBinaryResponse(200, 'OK', headers, within as never, source)).toBeInstanceOf(Response);
    const blob = new Blob([within]);
    expect(toBinaryResponse(200, 'OK', headers, blob as never, source)).toBeInstanceOf(Response);
    expect(
      toBinaryResponse(200, 'OK', headers, new ArrayBuffer(BINARY_BODY_LIMIT_BYTES), source),
    ).toBeInstanceOf(Response);
  });

  it('refuses an over-cap view, Blob, ArrayBuffer and unknown body in the adapter with the same rule', () => {
    const headers = new HttpHeaders();
    const source = final(null);
    const tooBig = new Uint8Array(OVER);
    expect(() => toBinaryResponse(200, 'OK', headers, tooBig as never, source)).toThrow(
      /exceeds limit/,
    );
    const blob = new Blob([tooBig]);
    expect(() => toBinaryResponse(200, 'OK', headers, blob as never, source)).toThrow(
      /exceeds limit/,
    );
    expect(() => toBinaryResponse(200, 'OK', headers, new ArrayBuffer(OVER), source)).toThrow(
      /exceeds limit/,
    );
    expect(() => toBinaryResponse(200, 'OK', headers, {} as never, source)).toThrow(
      /exceeds limit/,
    );
  });
});
