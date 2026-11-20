import { describe, expect, it } from 'vitest';

import { boundedResponse, readBoundedText, ResponseTooLargeError } from './boundedResponse.js';

interface Counting {
  readonly response: Response;
  readonly pulled: () => number;
  readonly cancelled: () => boolean;
}

/** A body of `chunks` that records every byte it hands out and whether it was cancelled. */
function counting(chunks: readonly Uint8Array[], headers: Record<string, string> = {}): Counting {
  let index = 0;
  let bytes = 0;
  let cancelled = false;
  const stream = new ReadableStream<Uint8Array>(
    {
      pull(controller) {
        const chunk = chunks[index];
        index += 1;
        if (chunk === undefined) {
          controller.close();
          return;
        }
        bytes += chunk.byteLength;
        controller.enqueue(chunk);
      },
      cancel() {
        cancelled = true;
      },
    },
    { highWaterMark: 0 },
  );
  return {
    response: new Response(stream, { headers }),
    pulled: () => bytes,
    cancelled: () => cancelled,
  };
}

const chunkOf = (size: number, fill = 0x61): Uint8Array => new Uint8Array(size).fill(fill);

describe('boundedResponse', () => {
  it('refuses an over-cap body after reading at most the cap plus one chunk, and cancels', async () => {
    const source = counting(Array.from({ length: 100 }, () => chunkOf(10)));

    await expect(boundedResponse(source.response, 25).text()).rejects.toBeInstanceOf(
      ResponseTooLargeError,
    );

    expect(source.pulled()).toBeLessThanOrEqual(25 + 10);
    expect(source.cancelled()).toBe(true);
  });

  it('passes a body of exactly the cap and refuses one byte more', async () => {
    const exact = counting([chunkOf(10), chunkOf(15)]);
    await expect(readBoundedText(exact.response, 25)).resolves.toBe('a'.repeat(25));

    const over = counting([chunkOf(10), chunkOf(16)]);
    await expect(readBoundedText(over.response, 25)).rejects.toBeInstanceOf(ResponseTooLargeError);
  });

  it('caps a body with no Content-Length', async () => {
    const source = counting([chunkOf(30)]);
    expect(source.response.headers.get('content-length')).toBeNull();

    await expect(readBoundedText(source.response, 25)).rejects.toBeInstanceOf(
      ResponseTooLargeError,
    );
  });

  it('caps a body whose Content-Length claims it is small', async () => {
    const source = counting([chunkOf(30)], { 'content-length': '5' });

    await expect(readBoundedText(source.response, 25)).rejects.toBeInstanceOf(
      ResponseTooLargeError,
    );
  });

  it('refuses a large declared length without pulling a body byte', async () => {
    const source = counting([chunkOf(10)], { 'content-length': '1000000' });

    await expect(readBoundedText(source.response, 25)).rejects.toBeInstanceOf(
      ResponseTooLargeError,
    );

    expect(source.pulled()).toBe(0);
    expect(source.cancelled()).toBe(true);
  });
});

describe('boundedResponse decoding', () => {
  it('decodes multi-byte UTF-8 split across chunks like the unbounded read', async () => {
    const text = 'héllo 日本語 😀 end';
    const bytes = new TextEncoder().encode(text);
    // One byte per chunk splits every multi-byte sequence.
    const chunks = Array.from(bytes, (byte) => Uint8Array.of(byte));

    const bounded = await readBoundedText(counting(chunks).response, 1024);
    const unbounded = await new Response(bytes).text();

    expect(bounded).toBe(unbounded);
    expect(bounded).toBe(text);
  });
});

describe('boundedResponse answers', () => {
  it('keeps status, status text and headers', () => {
    const original = new Response('x', { status: 418, statusText: 'teapot', headers: { a: 'b' } });

    const wrapped = boundedResponse(original);

    expect([wrapped.status, wrapped.statusText, wrapped.headers.get('a')]).toEqual([
      418,
      'teapot',
      'b',
    ]);
  });

  it('returns a bodiless answer unchanged', () => {
    const original = new Response(null, { status: 204 });

    expect(boundedResponse(original)).toBe(original);
  });

  it('carries no body byte, URL or token in the refusal text', async () => {
    const secret = 'egt_secret_value https://evil.example/path';
    const source = counting([new TextEncoder().encode(secret.repeat(10))]);

    const error: unknown = await readBoundedText(source.response, 20).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ResponseTooLargeError);
    const text = JSON.stringify([
      (error as ResponseTooLargeError).message,
      (error as ResponseTooLargeError).hint,
    ]);
    expect(text).not.toContain('egt_');
    expect(text).not.toContain('evil.example');
  });
});

describe('boundedResponse edge cases', () => {
  it.each([[25], [10, 15], [24, 1], [0, 12, 0, 13, 0]])(
    'accepts exact-cap chunk layout %j',
    async (...sizes: number[]) => {
      const source = counting(sizes.map((size) => chunkOf(size)));

      await expect(readBoundedText(source.response, 25)).resolves.toBe('a'.repeat(25));
    },
  );

  it('does not count empty chunks as bytes before refusing an overflow', async () => {
    const source = counting([0, 10, 0, 15, 0, 1, 100].map((size) => chunkOf(size)));

    await expect(readBoundedText(source.response, 25)).rejects.toBeInstanceOf(
      ResponseTooLargeError,
    );

    expect(source.pulled()).toBe(26);
    expect(source.cancelled()).toBe(true);
  });

  it('ignores a Content-Length that describes an encoded body', async () => {
    const source = counting([chunkOf(5)], {
      'content-length': '1000000',
      'content-encoding': 'gzip',
    });

    await expect(readBoundedText(source.response, 25)).resolves.toBe('aaaaa');
  });

  it('refuses a status Response cannot represent, cancelling the body, without a transport error', () => {
    const source = counting([chunkOf(5)]);
    const hostile = Object.defineProperty(source.response, 'status', { value: 600 });

    expect(() => boundedResponse(hostile)).toThrowError(
      expect.objectContaining({ code: 'request_failed' }) as Error,
    );
  });
});
